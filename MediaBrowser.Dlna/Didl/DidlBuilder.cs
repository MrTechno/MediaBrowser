﻿using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Entities;
using System;
using System.Globalization;
using System.Linq;
using System.Xml;

namespace MediaBrowser.Dlna.Didl
{
    public class DidlBuilder
    {
        private readonly CultureInfo _usCulture = new CultureInfo("en-US");
        
        private const string NS_DIDL = "urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/";
        private const string NS_DC = "http://purl.org/dc/elements/1.1/";
        private const string NS_UPNP = "urn:schemas-upnp-org:metadata-1-0/upnp/";
        private const string NS_DLNA = "urn:schemas-dlna-org:metadata-1-0/";

        private readonly DeviceProfile _profile;
        private readonly IImageProcessor _imageProcessor;
        private readonly string _serverAddress;
        private readonly IDtoService _dtoService;

        public DidlBuilder(DeviceProfile profile, IImageProcessor imageProcessor, string serverAddress, IDtoService dtoService)
        {
            _profile = profile;
            _imageProcessor = imageProcessor;
            _serverAddress = serverAddress;
            _dtoService = dtoService;
        }

        public string GetItemDidl(BaseItem item, string deviceId, Filter filter)
        {
            var result = new XmlDocument();

            var didl = result.CreateElement(string.Empty, "DIDL-Lite", NS_DIDL);
            didl.SetAttribute("xmlns:dc", NS_DC);
            didl.SetAttribute("xmlns:dlna", NS_DLNA);
            didl.SetAttribute("xmlns:upnp", NS_UPNP);
            //didl.SetAttribute("xmlns:sec", NS_SEC);

            foreach (var att in _profile.XmlRootAttributes)
            {
                didl.SetAttribute(att.Name, att.Value);
            }

            result.AppendChild(didl);

            result.DocumentElement.AppendChild(GetItemElement(result, item, deviceId, filter));

            return result.DocumentElement.OuterXml;
        }

        public XmlElement GetItemElement(XmlDocument doc, BaseItem item, string deviceId, Filter filter)
        {
            var element = doc.CreateElement(string.Empty, "item", NS_DIDL);
            element.SetAttribute("restricted", "1");
            element.SetAttribute("id", item.Id.ToString("N"));

            if (item.Parent != null)
            {
                element.SetAttribute("parentID", item.Parent.Id.ToString("N"));
            }

            //AddBookmarkInfo(item, user, element);

            AddGeneralProperties(item, element, filter);

            // refID?
            // storeAttribute(itemNode, object, ClassProperties.REF_ID, false);

            var audio = item as Audio;
            if (audio != null)
            {
                AddAudioResource(element, audio, deviceId, filter);
            }

            var video = item as Video;
            if (video != null)
            {
                AddVideoResource(element, video, deviceId, filter);
            }

            AddCover(item, element);

            return element;
        }

        private void AddVideoResource(XmlElement container, Video video, string deviceId, Filter filter)
        {
            var res = container.OwnerDocument.CreateElement(string.Empty, "res", NS_DIDL);

            var sources = _dtoService.GetMediaSources(video);

            int? maxBitrateSetting = null;

            var streamInfo = new StreamBuilder().BuildVideoItem(new VideoOptions
            {
                ItemId = video.Id.ToString("N"),
                MediaSources = sources,
                Profile = _profile,
                DeviceId = deviceId,
                MaxBitrate = maxBitrateSetting
            });

            var url = streamInfo.ToDlnaUrl(_serverAddress);
            //res.AppendChild(container.OwnerDocument.CreateCDataSection(url));
            res.InnerText = url;

            var mediaSource = sources.First(i => string.Equals(i.Id, streamInfo.MediaSourceId));

            if (mediaSource.RunTimeTicks.HasValue)
            {
                res.SetAttribute("duration", TimeSpan.FromTicks(mediaSource.RunTimeTicks.Value).ToString("c", _usCulture));
            }

            if (filter.Contains("res@size"))
            {
                if (streamInfo.IsDirectStream || streamInfo.EstimateContentLength)
                {
                    var size = streamInfo.TargetSize;

                    if (size.HasValue)
                    {
                        res.SetAttribute("size", size.Value.ToString(_usCulture));
                    }
                }
            }

            var totalBitrate = streamInfo.TargetTotalBitrate;
            var targetSampleRate = streamInfo.TargetAudioSampleRate;
            var targetChannels = streamInfo.TargetAudioChannels;

            var targetWidth = streamInfo.TargetWidth;
            var targetHeight = streamInfo.TargetHeight;

            if (targetChannels.HasValue)
            {
                res.SetAttribute("nrAudioChannels", targetChannels.Value.ToString(_usCulture));
            }

            if (filter.Contains("res@resolution"))
            {
                if (targetWidth.HasValue && targetHeight.HasValue)
                {
                    res.SetAttribute("resolution", string.Format("{0}x{1}", targetWidth.Value, targetHeight.Value));
                }
            }

            if (targetSampleRate.HasValue)
            {
                res.SetAttribute("sampleFrequency", targetSampleRate.Value.ToString(_usCulture));
            }

            if (totalBitrate.HasValue)
            {
                res.SetAttribute("bitrate", totalBitrate.Value.ToString(_usCulture));
            }

            var mediaProfile = _profile.GetVideoMediaProfile(streamInfo.Container,
                streamInfo.AudioCodec,
                streamInfo.VideoCodec,
                streamInfo.TargetAudioBitrate,
                targetChannels,
                targetWidth,
                targetHeight,
                streamInfo.TargetVideoBitDepth,
                streamInfo.TargetVideoBitrate,
                streamInfo.TargetVideoProfile,
                streamInfo.TargetVideoLevel,
                streamInfo.TargetFramerate,
                streamInfo.TargetPacketLength,
                streamInfo.TargetTimestamp);

            var filename = url.Substring(0, url.IndexOf('?'));

            var mimeType = mediaProfile == null || string.IsNullOrEmpty(mediaProfile.MimeType)
               ? MimeTypes.GetMimeType(filename)
               : mediaProfile.MimeType;

            var contentFeatures = new ContentFeatureBuilder(_profile).BuildVideoHeader(streamInfo.Container,
                streamInfo.VideoCodec,
                streamInfo.AudioCodec,
                targetWidth,
                targetHeight,
                streamInfo.TargetVideoBitDepth,
                streamInfo.TargetVideoBitrate,
                streamInfo.TargetAudioChannels,
                streamInfo.TargetAudioBitrate,
                streamInfo.TargetTimestamp,
                streamInfo.IsDirectStream,
                streamInfo.RunTimeTicks,
                streamInfo.TargetVideoProfile,
                streamInfo.TargetVideoLevel,
                streamInfo.TargetFramerate,
                streamInfo.TargetPacketLength,
                streamInfo.TranscodeSeekInfo);

            res.SetAttribute("protocolInfo", String.Format(
                "http-get:*:{0}:{1}",
                mimeType,
                contentFeatures
                ));

            container.AppendChild(res);
        }

        private void AddAudioResource(XmlElement container, Audio audio, string deviceId, Filter filter)
        {
            var res = container.OwnerDocument.CreateElement(string.Empty, "res", NS_DIDL);

            var sources = _dtoService.GetMediaSources(audio);

            var streamInfo = new StreamBuilder().BuildAudioItem(new AudioOptions
            {
                ItemId = audio.Id.ToString("N"),
                MediaSources = sources,
                Profile = _profile,
                DeviceId = deviceId
            });

            var url = streamInfo.ToDlnaUrl(_serverAddress);
            //res.AppendChild(container.OwnerDocument.CreateCDataSection(url));
            res.InnerText = url;

            var mediaSource = sources.First(i => string.Equals(i.Id, streamInfo.MediaSourceId));

            if (mediaSource.RunTimeTicks.HasValue)
            {
                res.SetAttribute("duration", TimeSpan.FromTicks(mediaSource.RunTimeTicks.Value).ToString("c", _usCulture));
            }

            if (filter.Contains("res@size"))
            {
                if (streamInfo.IsDirectStream || streamInfo.EstimateContentLength)
                {
                    var size = streamInfo.TargetSize;

                    if (size.HasValue)
                    {
                        res.SetAttribute("size", size.Value.ToString(_usCulture));
                    }
                }
            }

            var targetAudioBitrate = streamInfo.TargetAudioBitrate;
            var targetSampleRate = streamInfo.TargetAudioSampleRate;
            var targetChannels = streamInfo.TargetAudioChannels;

            if (targetChannels.HasValue)
            {
                res.SetAttribute("nrAudioChannels", targetChannels.Value.ToString(_usCulture));
            }

            if (targetSampleRate.HasValue)
            {
                res.SetAttribute("sampleFrequency", targetSampleRate.Value.ToString(_usCulture));
            }

            if (targetAudioBitrate.HasValue)
            {
                res.SetAttribute("bitrate", targetAudioBitrate.Value.ToString(_usCulture));
            }

            var mediaProfile = _profile.GetAudioMediaProfile(streamInfo.Container,
                streamInfo.AudioCodec,
                targetChannels,
                targetAudioBitrate);

            var filename = url.Substring(0, url.IndexOf('?'));

            var mimeType = mediaProfile == null || string.IsNullOrEmpty(mediaProfile.MimeType)
                ? MimeTypes.GetMimeType(filename)
                : mediaProfile.MimeType;

            var contentFeatures = new ContentFeatureBuilder(_profile).BuildAudioHeader(streamInfo.Container,
                streamInfo.TargetAudioCodec,
                targetAudioBitrate,
                targetSampleRate,
                targetChannels,
                streamInfo.IsDirectStream,
                streamInfo.RunTimeTicks,
                streamInfo.TranscodeSeekInfo);

            res.SetAttribute("protocolInfo", String.Format(
                "http-get:*:{0}:{1}",
                mimeType,
                contentFeatures
                ));

            container.AppendChild(res);
        }
        
        public XmlElement GetFolderElement(XmlDocument doc, Folder folder, int childCount, Filter filter)
        {
            var container = doc.CreateElement(string.Empty, "container", NS_DIDL);
            container.SetAttribute("restricted", "0");
            container.SetAttribute("searchable", "1");
            container.SetAttribute("childCount", childCount.ToString(_usCulture));
            container.SetAttribute("id", folder.Id.ToString("N"));

            var parent = folder.Parent;
            if (parent == null)
            {
                container.SetAttribute("parentID", "0");
            }
            else
            {
                container.SetAttribute("parentID", parent.Id.ToString("N"));
            }

            AddCommonFields(folder, container, filter);

            AddCover(folder, container);

            return container;
        }

        //private void AddBookmarkInfo(BaseItem item, User user, XmlElement element)
        //{
        //    var userdata = _userDataManager.GetUserData(user.Id, item.GetUserDataKey());

        //    if (userdata.PlaybackPositionTicks > 0)
        //    {
        //        var dcmInfo = element.OwnerDocument.CreateElement("sec", "dcmInfo", NS_SEC);
        //        dcmInfo.InnerText = string.Format("BM={0}", Convert.ToInt32(TimeSpan.FromTicks(userdata.PlaybackPositionTicks).TotalSeconds).ToString(_usCulture));
        //        element.AppendChild(dcmInfo);
        //    }
        //}

        /// <summary>
        /// Adds fields used by both items and folders
        /// </summary>
        /// <param name="item">The item.</param>
        /// <param name="element">The element.</param>
        /// <param name="filter">The filter.</param>
        private void AddCommonFields(BaseItem item, XmlElement element, Filter filter)
        {
            if (filter.Contains("dc:title"))
            {
                AddValue(element, "dc", "title", item.Name, NS_DC);
            }

            element.AppendChild(CreateObjectClass(element.OwnerDocument, item));

            if (filter.Contains("dc:date"))
            {
                if (item.PremiereDate.HasValue)
                {
                    AddValue(element, "dc", "date", item.PremiereDate.Value.ToString("o"), NS_DC);
                }
            }

            foreach (var genre in item.Genres)
            {
                AddValue(element, "upnp", "genre", genre, NS_UPNP);
            }

            foreach (var studio in item.Studios)
            {
                AddValue(element, "upnp", "publisher", studio, NS_UPNP);
            }

            if (filter.Contains("dc:description"))
            {
                if (!string.IsNullOrWhiteSpace(item.Overview))
                {
                    AddValue(element, "dc", "description", item.Overview, NS_DC);
                }
            }
            if (filter.Contains("upnp:longDescription"))
            {
                if (!string.IsNullOrWhiteSpace(item.Overview))
                {
                    AddValue(element, "upnp", "longDescription", item.Overview, NS_UPNP);
                }
            }

            if (!string.IsNullOrEmpty(item.OfficialRating))
            {
                if (filter.Contains("dc:rating"))
                {
                    AddValue(element, "dc", "rating", item.OfficialRating, NS_DC);
                }
                if (filter.Contains("upnp:rating"))
                {
                    AddValue(element, "upnp", "rating", item.OfficialRating, NS_UPNP);
                }
            }

            AddPeople(item, element);
        }

        private XmlElement CreateObjectClass(XmlDocument result, BaseItem item)
        {
            var objectClass = result.CreateElement("upnp", "class", NS_UPNP);

            if (item.IsFolder)
            {
                string classType = null;

                if (!_profile.RequiresPlainFolders)
                {
                    if (item is MusicAlbum)
                    {
                        classType = "object.container.album.musicAlbum";
                    }
                    if (item is MusicArtist)
                    {
                        classType = "object.container.person.musicArtist";
                    }
                }

                objectClass.InnerText = classType ?? "object.container.storageFolder";
            }
            else if (string.Equals(item.MediaType, MediaType.Audio, StringComparison.OrdinalIgnoreCase))
            {
                objectClass.InnerText = "object.item.audioItem.musicTrack";
            }
            else if (string.Equals(item.MediaType, MediaType.Photo, StringComparison.OrdinalIgnoreCase))
            {
                objectClass.InnerText = "object.item.imageItem.photo";
            }
            else if (string.Equals(item.MediaType, MediaType.Video, StringComparison.OrdinalIgnoreCase))
            {
                if (!_profile.RequiresPlainVideoItems && item is Movie)
                {
                    objectClass.InnerText = "object.item.videoItem.movie";
                }
                else
                {
                    objectClass.InnerText = "object.item.videoItem";
                }
            }
            else
            {
                throw new NotSupportedException();
            }

            return objectClass;
        }

        private void AddPeople(BaseItem item, XmlElement element)
        {
            foreach (var actor in item.People)
            {
                AddValue(element, "upnp", (actor.Type ?? PersonType.Actor).ToLower(), actor.Name, NS_UPNP);
            }
        }

        private void AddGeneralProperties(BaseItem item, XmlElement element, Filter filter)
        {
            AddCommonFields(item, element, filter);

            var audio = item as Audio;

            if (audio != null)
            {
                foreach (var artist in audio.Artists)
                {
                    AddValue(element, "upnp", "artist", artist, NS_UPNP);
                }

                if (!string.IsNullOrEmpty(audio.Album))
                {
                    AddValue(element, "upnp", "album", audio.Album, NS_UPNP);
                }

                if (!string.IsNullOrEmpty(audio.AlbumArtist))
                {
                    AddValue(element, "upnp", "albumArtist", audio.AlbumArtist, NS_UPNP);
                }
            }

            var album = item as MusicAlbum;

            if (album != null)
            {
                if (!string.IsNullOrEmpty(album.AlbumArtist))
                {
                    AddValue(element, "upnp", "artist", album.AlbumArtist, NS_UPNP);
                    AddValue(element, "upnp", "albumArtist", album.AlbumArtist, NS_UPNP);
                }
            }

            var musicVideo = item as MusicVideo;

            if (musicVideo != null)
            {
                if (!string.IsNullOrEmpty(musicVideo.Artist))
                {
                    AddValue(element, "upnp", "artist", musicVideo.Artist, NS_UPNP);
                }

                if (!string.IsNullOrEmpty(musicVideo.Album))
                {
                    AddValue(element, "upnp", "album", musicVideo.Album, NS_UPNP);
                }
            }

            if (item.IndexNumber.HasValue)
            {
                AddValue(element, "upnp", "originalTrackNumber", item.IndexNumber.Value.ToString(_usCulture), NS_UPNP);
            }
        }

        private void AddValue(XmlElement elem, string prefix, string name, string value, string namespaceUri)
        {
            try
            {
                var date = elem.OwnerDocument.CreateElement(prefix, name, namespaceUri);
                date.InnerText = value;
                elem.AppendChild(date);
            }
            catch (XmlException)
            {
                //_logger.Error("Error adding xml value: " + value);
            }
        }

        private void AddCover(BaseItem item, XmlElement element)
        {
            var imageInfo = GetImageInfo(item);

            if (imageInfo == null)
            {
                return;
            }

            var result = element.OwnerDocument;

            var albumartUrlInfo = GetImageUrl(imageInfo, _profile.MaxAlbumArtWidth, _profile.MaxAlbumArtHeight);

            var icon = result.CreateElement("upnp", "albumArtURI", NS_UPNP);
            var profile = result.CreateAttribute("dlna", "profileID", NS_DLNA);
            profile.InnerText = _profile.AlbumArtPn;
            icon.SetAttributeNode(profile);
            icon.InnerText = albumartUrlInfo.Url;
            element.AppendChild(icon);

            var iconUrlInfo = GetImageUrl(imageInfo, _profile.MaxIconWidth, _profile.MaxIconHeight);
            icon = result.CreateElement("upnp", "icon", NS_UPNP);
            profile = result.CreateAttribute("dlna", "profileID", NS_DLNA);
            profile.InnerText = _profile.AlbumArtPn;
            icon.SetAttributeNode(profile);
            icon.InnerText = iconUrlInfo.Url;
            element.AppendChild(icon);

            if (!_profile.EnableAlbumArtInDidl)
            {
                return;
            }

            var res = result.CreateElement(string.Empty, "res", NS_DIDL);

            res.InnerText = albumartUrlInfo.Url;

            var width = albumartUrlInfo.Width;
            var height = albumartUrlInfo.Height;

            var contentFeatures = new ContentFeatureBuilder(_profile).BuildImageHeader("jpg", width, height);

            res.SetAttribute("protocolInfo", String.Format(
                "http-get:*:{0}:{1}",
                "image/jpeg",
                contentFeatures
                ));

            if (width.HasValue && height.HasValue)
            {
                res.SetAttribute("resolution", string.Format("{0}x{1}", width.Value, height.Value));
            }

            element.AppendChild(res);
        }

        private ImageDownloadInfo GetImageInfo(BaseItem item)
        {
            if (item.HasImage(ImageType.Primary))
            {
                return GetImageInfo(item, ImageType.Primary);
            }
            if (item.HasImage(ImageType.Thumb))
            {
                return GetImageInfo(item, ImageType.Thumb);
            }

            if (item is Audio || item is Episode)
            {
                item = item.Parents.FirstOrDefault(i => i.HasImage(ImageType.Primary));

                if (item != null)
                {
                    return GetImageInfo(item, ImageType.Primary);
                }
            }

            return null;
        }

        private ImageDownloadInfo GetImageInfo(BaseItem item, ImageType type)
        {
            var imageInfo = item.GetImageInfo(type, 0);
            string tag = null;

            try
            {
                var guid = _imageProcessor.GetImageCacheTag(item, ImageType.Primary);

                tag = guid.HasValue ? guid.Value.ToString("N") : null;
            }
            catch
            {

            }

            int? width = null;
            int? height = null;

            try
            {
                var size = _imageProcessor.GetImageSize(imageInfo.Path, imageInfo.DateModified);

                width = Convert.ToInt32(size.Width);
                height = Convert.ToInt32(size.Height);
            }
            catch
            {

            }

            return new ImageDownloadInfo
            {
                ItemId = item.Id.ToString("N"),
                Type = ImageType.Primary,
                ImageTag = tag,
                Width = width,
                Height = height
            };
        }

        class ImageDownloadInfo
        {
            internal string ItemId;
            internal string ImageTag;
            internal ImageType Type;

            internal int? Width;
            internal int? Height;
        }

        class ImageUrlInfo
        {
            internal string Url;

            internal int? Width;
            internal int? Height;
        }

        private ImageUrlInfo GetImageUrl(ImageDownloadInfo info, int? maxWidth, int? maxHeight)
        {
            var url = string.Format("{0}/Items/{1}/Images/{2}?tag={3}&format=jpg",
                _serverAddress,
                info.ItemId,
                info.Type,
                info.ImageTag);

            if (maxWidth.HasValue)
            {
                url += "&maxWidth=" + maxWidth.Value.ToString(_usCulture);
            }

            if (maxHeight.HasValue)
            {
                url += "&maxHeight=" + maxHeight.Value.ToString(_usCulture);
            }

            var width = info.Width;
            var height = info.Height;

            if (width.HasValue && height.HasValue)
            {
                if (maxWidth.HasValue || maxHeight.HasValue)
                {
                    var newSize = DrawingUtils.Resize(new ImageSize
                    {
                        Height = height.Value,
                        Width = width.Value

                    }, maxWidth: maxWidth, maxHeight: maxHeight);

                    width = Convert.ToInt32(newSize.Width);
                    height = Convert.ToInt32(newSize.Height);
                }
            }

            return new ImageUrlInfo
            {
                Url = url,
                Width = width,
                Height = height
            };
        }
    }
}
