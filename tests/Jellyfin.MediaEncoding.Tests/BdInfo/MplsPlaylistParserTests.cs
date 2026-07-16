using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using MediaBrowser.MediaEncoding.BdInfo;
using Xunit;

namespace Jellyfin.MediaEncoding.Tests.BdInfo;

public class MplsPlaylistParserTests
{
    [Theory]
    [InlineData("0100")]
    [InlineData("0200")]
    [InlineData("0300")]
    public void Parse_AcceptsDefinedMplsVersions(string version)
    {
        var path = WriteBytes(CreatePlaylist(CreatePlayItem("00001", 1, 0, 0, 90_000), version));

        try
        {
            Assert.Equal(version, MplsPlaylistParser.Parse(path).Version);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Parse_PreservesRawNavigationTimestampsAndAuthoringFields()
    {
        var path = WritePlaylist(
            CreatePlayItem(
                "00123",
                connectionCondition: 5,
                stcId: 7,
                inTime: 45_001,
                outTime: 135_007,
                stillMode: 1,
                stillTime: 250));

        try
        {
            var playlist = MplsPlaylistParser.Parse(path);

            Assert.Equal("0200", playlist.Version);
            Assert.Equal(0, playlist.SubPathCount);
            var item = Assert.Single(playlist.PlayItems);
            Assert.Equal("00123", item.ClipId);
            Assert.Equal("M2TS", item.CodecIdentifier);
            Assert.Equal(5, item.ConnectionCondition);
            Assert.Equal(7, item.StcId);
            Assert.Equal(45_001U, item.InTime45Khz);
            Assert.Equal(135_007U, item.OutTime45Khz);
            Assert.Equal(1, item.StillMode);
            Assert.Equal(250, item.StillTime);
            Assert.False(item.IsMultiAngle);
            Assert.Equal(1, item.AngleCount);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Parse_MultiAngleItem_SkipsAdditionalAngleRecords()
    {
        var path = WritePlaylist(
            CreatePlayItem(
                "00001",
                connectionCondition: 1,
                stcId: 0,
                inTime: 0,
                outTime: 90_000,
                angleCount: 3));

        try
        {
            var item = Assert.Single(MplsPlaylistParser.Parse(path).PlayItems);

            Assert.True(item.IsMultiAngle);
            Assert.Equal(3, item.AngleCount);
            Assert.Equal(90_000U, item.OutTime45Khz);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Parse_PlayItemExtendsPastPlaylist_ThrowsInvalidDataException()
    {
        var bytes = CreatePlaylist(CreatePlayItem("00001", 1, 0, 0, 90_000));
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(50, 2), ushort.MaxValue);
        var path = WriteBytes(bytes);

        try
        {
            Assert.Throws<InvalidDataException>(() => MplsPlaylistParser.Parse(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Parse_OutOfRangePlaylistOffset_ThrowsInvalidDataException()
    {
        var bytes = CreatePlaylist(CreatePlayItem("00001", 1, 0, 0, 90_000));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8, 4), uint.MaxValue);
        var path = WriteBytes(bytes);

        try
        {
            Assert.Throws<InvalidDataException>(() => MplsPlaylistParser.Parse(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string WritePlaylist(byte[] playItem)
        => WriteBytes(CreatePlaylist(playItem));

    private static string WriteBytes(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.mpls");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static byte[] CreatePlaylist(byte[] playItem, string version = "0200")
    {
        const int playlistOffset = 40;
        var playlistPayloadLength = 6 + playItem.Length;
        var bytes = new byte[playlistOffset + 4 + playlistPayloadLength];
        Encoding.ASCII.GetBytes("MPLS" + version).CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8, 4), playlistOffset);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(playlistOffset, 4), (uint)playlistPayloadLength);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(playlistOffset + 6, 2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(playlistOffset + 8, 2), 0);
        playItem.CopyTo(bytes, playlistOffset + 10);
        return bytes;
    }

    private static byte[] CreatePlayItem(
        string clipId,
        int connectionCondition,
        byte stcId,
        uint inTime,
        uint outTime,
        byte stillMode = 0,
        ushort stillTime = 0,
        byte angleCount = 1)
    {
        var isMultiAngle = angleCount > 1;
        var payloadLength = 32 + (isMultiAngle ? 2 + ((angleCount - 1) * 10) : 0);
        var bytes = new byte[payloadLength + 2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(0, 2), (ushort)payloadLength);
        Encoding.ASCII.GetBytes(clipId).CopyTo(bytes, 2);
        Encoding.ASCII.GetBytes("M2TS").CopyTo(bytes, 7);
        var flags = connectionCondition & 0x0F;
        if (isMultiAngle)
        {
            flags |= 0x10;
        }

        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(11, 2), (ushort)flags);
        bytes[13] = stcId;
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(14, 4), inTime);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(18, 4), outTime);
        bytes[31] = stillMode;
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(32, 2), stillTime);
        if (isMultiAngle)
        {
            bytes[34] = angleCount;
        }

        return bytes;
    }
}
