using System;
using System.Globalization;
using Jellyfin.Api.Helpers;
using Jellyfin.Data.Enums;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Devices;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Api.Tests.Helpers
{
    public class MediaInfoHelperTests
    {
        private static MediaInfoHelper CreateHelper()
        {
            return new MediaInfoHelper(
                Mock.Of<IUserManager>(),
                Mock.Of<ILibraryManager>(),
                Mock.Of<IMediaSourceManager>(),
                Mock.Of<IMediaEncoder>(),
                Mock.Of<IServerConfigurationManager>(),
                Mock.Of<ILogger<MediaInfoHelper>>(),
                Mock.Of<INetworkManager>(),
                Mock.Of<IDeviceManager>());
        }

        private static MediaSourceInfo CreateSource(Guid itemId, int bitrate, bool supportsDirectPlay = true)
        {
            return new MediaSourceInfo
            {
                Id = itemId.ToString("N", CultureInfo.InvariantCulture),
                Protocol = MediaProtocol.File,
                Bitrate = bitrate,
                SupportsDirectPlay = supportsDirectPlay,
                SupportsDirectStream = true,
                SupportsTranscoding = true
            };
        }

        [Fact]
        public void SortMediaSources_PreferredItemExceedsBitrate_StaysDefault()
        {
            // The version the user was watching (the queried item) must stay the default
            // even when a sibling version fits the bitrate limit better, since the resume
            // position belongs to that exact version.
            var preferredItemId = Guid.NewGuid();
            var preferredSource = CreateSource(preferredItemId, bitrate: 80_000_000, supportsDirectPlay: false);
            var siblingSource = CreateSource(Guid.NewGuid(), bitrate: 8_000_000);

            var result = new PlaybackInfoResponse
            {
                MediaSources = [siblingSource, preferredSource]
            };

            CreateHelper().SortMediaSources(result, maxBitrate: 20_000_000, preferredItemId);

            Assert.Equal(preferredSource.Id, result.MediaSources[0].Id);
        }

        [Fact]
        public void SortMediaSources_NoPreferredItem_OrdersByPlayability()
        {
            var directPlay = CreateSource(Guid.NewGuid(), bitrate: 8_000_000);
            var transcodeOnly = CreateSource(Guid.NewGuid(), bitrate: 8_000_000, supportsDirectPlay: false);
            transcodeOnly.SupportsDirectStream = false;

            var result = new PlaybackInfoResponse
            {
                MediaSources = [transcodeOnly, directPlay]
            };

            CreateHelper().SortMediaSources(result, maxBitrate: 20_000_000);

            Assert.Equal(directPlay.Id, result.MediaSources[0].Id);
        }

        [Fact]
        public void SortMediaSources_PreferredIdNotInSources_KeepsPlayabilityOrder()
        {
            var directPlay = CreateSource(Guid.NewGuid(), bitrate: 8_000_000);
            var transcodeOnly = CreateSource(Guid.NewGuid(), bitrate: 8_000_000, supportsDirectPlay: false);
            transcodeOnly.SupportsDirectStream = false;

            var result = new PlaybackInfoResponse
            {
                MediaSources = [transcodeOnly, directPlay]
            };

            CreateHelper().SortMediaSources(result, maxBitrate: 20_000_000, Guid.NewGuid());

            Assert.Equal(directPlay.Id, result.MediaSources[0].Id);
        }

        [Fact]
        public void GetEffectiveDeviceProfile_InfuseBluRayWithoutHls_AddsRequestLocalHlsProfile()
        {
            var httpProfile = new TranscodingProfile
            {
                Container = "ts",
                Type = DlnaProfileType.Video,
                VideoCodec = "h264,hevc",
                AudioCodec = "aac",
                Protocol = MediaStreamProtocol.http,
                Context = EncodingContext.Streaming
            };
            var profile = new DeviceProfile
            {
                Name = "Infuse",
                MaxStreamingBitrate = 100_000_000,
                TranscodingProfiles = [httpProfile]
            };

            var result = MediaInfoHelper.GetEffectiveDeviceProfile(profile, VideoType.BluRay, "Infuse-Direct");

            Assert.NotSame(profile, result);
            Assert.Single(profile.TranscodingProfiles);
            Assert.Equal(2, result.TranscodingProfiles.Length);
            Assert.Same(httpProfile, result.TranscodingProfiles[1]);
            Assert.Equal(profile.Name, result.Name);
            Assert.Equal(profile.MaxStreamingBitrate, result.MaxStreamingBitrate);

            var hlsProfile = result.TranscodingProfiles[0];
            Assert.Equal(DlnaProfileType.Video, hlsProfile.Type);
            Assert.Equal(EncodingContext.Streaming, hlsProfile.Context);
            Assert.Equal(MediaStreamProtocol.hls, hlsProfile.Protocol);
            Assert.Equal("mp4", hlsProfile.Container);
            Assert.Equal("h264,hevc", hlsProfile.VideoCodec);
            Assert.Equal("aac,eac3,ac3", hlsProfile.AudioCodec);
            Assert.Equal("6", hlsProfile.MaxAudioChannels);
            Assert.Equal(6, hlsProfile.SegmentLength);
        }

        [Theory]
        [InlineData(VideoType.VideoFile, "Infuse-Direct")]
        [InlineData(VideoType.BluRay, "Jellyfin Desktop")]
        [InlineData(VideoType.BluRay, null)]
        public void GetEffectiveDeviceProfile_NonMatchingRequest_ReturnsOriginalProfile(
            VideoType videoType,
            string? clientName)
        {
            var profile = new DeviceProfile
            {
                TranscodingProfiles =
                [
                    new TranscodingProfile
                    {
                        Type = DlnaProfileType.Video,
                        Protocol = MediaStreamProtocol.http,
                        Context = EncodingContext.Streaming
                    }
                ]
            };

            var result = MediaInfoHelper.GetEffectiveDeviceProfile(profile, videoType, clientName);

            Assert.Same(profile, result);
        }

        [Fact]
        public void GetEffectiveDeviceProfile_InfuseBluRayWithHls_ReturnsOriginalProfile()
        {
            var profile = new DeviceProfile
            {
                TranscodingProfiles =
                [
                    new TranscodingProfile
                    {
                        Type = DlnaProfileType.Video,
                        Protocol = MediaStreamProtocol.hls,
                        Context = EncodingContext.Streaming
                    }
                ]
            };

            var result = MediaInfoHelper.GetEffectiveDeviceProfile(profile, VideoType.BluRay, "infuse-direct");

            Assert.Same(profile, result);
        }
    }
}
