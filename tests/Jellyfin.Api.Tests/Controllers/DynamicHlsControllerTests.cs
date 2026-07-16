using System;
using System.IO;
using Jellyfin.Api.Controllers;
using Jellyfin.Api.Helpers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Xunit;

namespace Jellyfin.Api.Tests.Controllers
{
    public class DynamicHlsControllerTests
    {
        [Theory]
        [MemberData(nameof(GetSegmentLengths_Success_TestData))]
        public void GetSegmentLengths_Success(long runtimeTicks, int segmentlength, double[] expected)
        {
            var res = DynamicHlsController.GetSegmentLengthsInternal(runtimeTicks, segmentlength);
            Assert.Equal(expected.Length, res.Length);
            for (int i = 0; i < expected.Length; i++)
            {
                Assert.Equal(expected[i], res[i]);
            }
        }

        public static TheoryData<long, int, double[]> GetSegmentLengths_Success_TestData()
        {
            var data = new TheoryData<long, int, double[]>();
            data.Add(0, 6, Array.Empty<double>());
            data.Add(
                TimeSpan.FromSeconds(3).Ticks,
                6,
                new double[] { 3 });
            data.Add(
                TimeSpan.FromSeconds(6).Ticks,
                6,
                new double[] { 6 });
            data.Add(
                TimeSpan.FromSeconds(3.3333333).Ticks,
                6,
                new double[] { 3.3333333 });
            data.Add(
                TimeSpan.FromSeconds(9.3333333).Ticks,
                6,
                new double[] { 6, 3.3333333 });

            return data;
        }

        [Theory]
        [InlineData(null, true)]
        [InlineData(VideoType.VideoFile, true)]
        [InlineData(VideoType.Iso, true)]
        [InlineData(VideoType.Dvd, true)]
        [InlineData(VideoType.BluRay, false)]
        public void ShouldPreserveInputTimestamps_OnlyNormalizesBluRay(VideoType? videoType, bool expected)
        {
            Assert.Equal(expected, DynamicHlsController.ShouldPreserveInputTimestamps(videoType));
        }

        [Theory]
        [InlineData(VideoType.BluRay, 1L, true)]
        [InlineData(VideoType.BluRay, 0L, false)]
        [InlineData(VideoType.BluRay, null, false)]
        [InlineData(VideoType.VideoFile, 1L, false)]
        [InlineData(null, 1L, false)]
        public void ShouldApplyOutputTimestampOffset_OnlyAppliesToBluRaySeek(
            VideoType? videoType,
            long? startTimeTicks,
            bool expected)
        {
            Assert.Equal(expected, DynamicHlsController.ShouldApplyOutputTimestampOffset(videoType, startTimeTicks));
        }

        [Theory]
        [InlineData(VideoType.BluRay, "copy", true)]
        [InlineData(VideoType.BluRay, "libx264", false)]
        [InlineData(VideoType.VideoFile, "copy", false)]
        [InlineData(null, "copy", false)]
        public void ShouldSplitBluRayRemuxSegments_OnlyAppliesToCopiedBluRayVideo(
            VideoType? videoType,
            string videoCodec,
            bool expected)
        {
            Assert.Equal(expected, DynamicHlsController.ShouldSplitBluRayRemuxSegments(videoType, videoCodec));
        }

        [Theory]
        [InlineData(null, true)]
        [InlineData(VideoType.VideoFile, true)]
        [InlineData(VideoType.Dvd, true)]
        [InlineData(VideoType.BluRay, false)]
        public void ShouldIncludeCompatibilityVideoVariants_SuppressesBluRayEncodeAlternatives(
            VideoType? videoType,
            bool expected)
        {
            Assert.Equal(expected, DynamicHlsHelper.ShouldIncludeCompatibilityVideoVariants(videoType));
        }

        [Theory]
        [InlineData(false, new string[0], "")]
        [InlineData(false, new[] { "output_ts_offset=300" }, "-hls_segment_options output_ts_offset=300")]
        [InlineData(
            false,
            new[] { "movflags=+frag_discont", "output_ts_offset=300.5" },
            "-hls_segment_options movflags=+frag_discont:output_ts_offset=300.5")]
        [InlineData(true, new[] { "output_ts_offset=300" }, "-hls_ts_options output_ts_offset=300")]
        public void BuildHlsSegmentOptionsArgument_UsesChildMuxerOptions(
            bool useLegacyOptionName,
            string[] options,
            string expected)
        {
            Assert.Equal(expected, DynamicHlsController.BuildHlsSegmentOptionsArgument(useLegacyOptionName, options));
        }

        [Theory]
        [InlineData(VideoType.BluRay, "mp4", 3, 225225, 1, 6)]
        [InlineData(VideoType.BluRay, "mp4", 3, 270000, 1, 7)]
        [InlineData(VideoType.BluRay, "mp4", 6, 225225, 1, 6)]
        [InlineData(VideoType.BluRay, "ts", 3, 225225, 1, 3)]
        [InlineData(VideoType.VideoFile, "mp4", 3, 225225, 1, 3)]
        [InlineData(VideoType.BluRay, "mp4", 3, 225225, null, 3)]
        public void GetBluRayFmp4SegmentLength_KeepsFirstSelectedStreamsInInitializationSegment(
            VideoType videoType,
            string segmentContainer,
            int segmentLength,
            long audioStart45Khz,
            int? audioStreamIndex,
            int expected)
        {
            var plan = new BluRayPlaybackPlan
            {
                PlaylistName = "00150.mpls",
                Streams =
                [
                    new BluRayPlaybackStream
                    {
                        Index = 0,
                        Type = MediaStreamType.Video,
                        Codec = "h264",
                        FirstPlayItemStart45Khz = 0
                    },
                    new BluRayPlaybackStream
                    {
                        Index = 1,
                        Type = MediaStreamType.Audio,
                        Codec = "ac3",
                        FirstPlayItemStart45Khz = audioStart45Khz
                    }
                ],
                PlayItems = [new BluRayPlaybackItem { ClipFileName = "00001.m2ts", OutTime45Khz = 90000 }]
            };

            var result = DynamicHlsController.GetBluRayFmp4SegmentLength(
                segmentLength,
                videoType,
                segmentContainer,
                plan,
                videoStreamIndex: 0,
                audioStreamIndex);

            Assert.Equal(expected, result);
        }

        [Fact]
        public void IsUsableSegmentFile_RequiresNonEmptyFile()
        {
            var tempDirectory = Directory.CreateTempSubdirectory("jellyfin-hls-test-");
            try
            {
                var segmentPath = Path.Combine(tempDirectory.FullName, "segment.ts");
                Assert.False(DynamicHlsController.IsUsableSegmentFile(segmentPath));

                File.WriteAllBytes(segmentPath, Array.Empty<byte>());
                Assert.False(DynamicHlsController.IsUsableSegmentFile(segmentPath));

                File.WriteAllBytes(segmentPath, new byte[] { 0x47 });
                Assert.True(DynamicHlsController.IsUsableSegmentFile(segmentPath));
            }
            finally
            {
                tempDirectory.Delete(true);
            }
        }
    }
}
