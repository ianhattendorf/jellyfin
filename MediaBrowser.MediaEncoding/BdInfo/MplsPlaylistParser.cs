using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MediaBrowser.MediaEncoding.BdInfo;

/// <summary>
/// Reads the small subset of MPLS authoring data needed to construct an exact concat timeline.
/// </summary>
internal static class MplsPlaylistParser
{
    public static MplsPlaylist Parse(string path)
    {
        var reader = new BigEndianReader(File.ReadAllBytes(path));
        if (!string.Equals(reader.ReadAscii(4), "MPLS", StringComparison.Ordinal))
        {
            throw new InvalidDataException("The file does not contain an MPLS header.");
        }

        var version = reader.ReadAscii(4);
        if (!string.Equals(version, "0100", StringComparison.Ordinal)
            && !string.Equals(version, "0200", StringComparison.Ordinal)
            && !string.Equals(version, "0300", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Unsupported MPLS version '{version}'.");
        }

        var playlistOffset = ToInt32(reader.ReadUInt32(), "playlist offset");
        reader.Position = playlistOffset;
        var playlistLength = ToInt32(reader.ReadUInt32(), "playlist length");
        var playlistEnd = AddChecked(reader.Position, playlistLength, "playlist length");
        reader.EnsurePosition(playlistEnd);
        reader.Skip(2);

        var playItemCount = reader.ReadUInt16();
        var subPathCount = reader.ReadUInt16();
        var playItems = new List<MplsPlayItem>(playItemCount);

        for (var index = 0; index < playItemCount; index++)
        {
            var itemLength = reader.ReadUInt16();
            var itemStart = reader.Position;
            var itemEnd = AddChecked(itemStart, itemLength, $"PlayItem {index} length");
            if (itemLength < 32 || itemEnd > playlistEnd)
            {
                throw new InvalidDataException($"MPLS PlayItem {index} has an invalid length.");
            }

            var clipId = reader.ReadAscii(5);
            var codecIdentifier = reader.ReadAscii(4);
            var flags = reader.ReadUInt16();
            var isMultiAngle = (flags & 0x10) != 0;
            var connectionCondition = flags & 0x0F;
            var stcId = reader.ReadByte();
            var inTime = reader.ReadUInt32();
            var outTime = reader.ReadUInt32();

            // User-operation mask, random-access flags, still mode, and still time.
            reader.Skip(8);
            reader.Skip(1);
            var stillMode = reader.ReadByte();
            var stillTime = reader.ReadUInt16();

            var angleCount = 1;
            if (isMultiAngle)
            {
                angleCount = Math.Max(reader.ReadByte(), (byte)1);
                reader.Skip(1);
                reader.Skip(checked((angleCount - 1) * 10));
            }

            if (reader.Position > itemEnd)
            {
                throw new InvalidDataException($"MPLS PlayItem {index} is truncated.");
            }

            reader.Position = itemEnd;
            playItems.Add(new MplsPlayItem(
                clipId,
                codecIdentifier,
                connectionCondition,
                stcId,
                inTime,
                outTime,
                stillMode,
                stillTime,
                isMultiAngle,
                angleCount));
        }

        return new MplsPlaylist(version, subPathCount, playItems);
    }

    private static int ToInt32(uint value, string fieldName)
    {
        if (value > int.MaxValue)
        {
            throw new InvalidDataException($"The MPLS {fieldName} is out of range.");
        }

        return (int)value;
    }

    private static int AddChecked(int left, int right, string fieldName)
    {
        if (left > int.MaxValue - right)
        {
            throw new InvalidDataException($"The MPLS {fieldName} is out of range.");
        }

        return left + right;
    }

    private sealed class BigEndianReader
    {
        private readonly byte[] _data;
        private int _position;

        public BigEndianReader(byte[] data)
        {
            _data = data;
        }

        public int Position
        {
            get => _position;
            set
            {
                EnsurePosition(value);
                _position = value;
            }
        }

        public byte ReadByte()
        {
            EnsureAvailable(1);
            return _data[_position++];
        }

        public ushort ReadUInt16()
        {
            EnsureAvailable(2);
            var value = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(_position, 2));
            _position += 2;
            return value;
        }

        public uint ReadUInt32()
        {
            EnsureAvailable(4);
            var value = BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(_position, 4));
            _position += 4;
            return value;
        }

        public string ReadAscii(int length)
        {
            EnsureAvailable(length);
            var value = Encoding.ASCII.GetString(_data, _position, length);
            _position += length;
            return value;
        }

        public void Skip(int length)
        {
            EnsureAvailable(length);
            _position += length;
        }

        public void EnsurePosition(int position)
        {
            if (position < 0 || position > _data.Length)
            {
                throw new InvalidDataException("The MPLS file contains an out-of-range offset.");
            }
        }

        private void EnsureAvailable(int length)
        {
            if (length < 0 || _position > _data.Length - length)
            {
                throw new InvalidDataException("The MPLS file is truncated.");
            }
        }
    }
}
