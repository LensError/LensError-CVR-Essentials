#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Threading.Tasks;
using ABI.CCK.Components;
using UnityEditor;
using UnityEngine;

namespace LensError.Editor
{
    public class YoutubePlaylistImporter : EditorWindow
    {
        private const string PrefApiKey  = "LensError_YtImporter_ApiKey";
        private const string YtApi       = "https://www.googleapis.com/youtube/v3/";
        private const int    ChunkSize   = 200;

        // API key section
        private string _apiKey = "";
        private bool _showApiKey;
        private bool _apiKeyFoldout = true;

        // Import config
        private string _playlistInput = "";
        private CVRVideoPlayer _targetPlayer;
        private bool _shuffleOnImport;
        private SortOrder _sortOrder;
        private bool _appendToExisting;

        // Scene player picker
        private CVRVideoPlayer[] _scenePlayers = new CVRVideoPlayer[0];
        private string[] _scenePlayerOptions = new[] { "None" };
        private int _scenePlayerIdx;

        // Fetch state
        private enum FetchState { Idle, Fetching, Done, Error }
        private FetchState _state;
        private string _status = "";
        private readonly List<VideoEntry> _videos = new List<VideoEntry>();
        private PlaylistMeta _meta;
        private int _fetchGen;

        // Thumbnails
        private readonly Dictionary<string, Texture2D> _thumbs = new Dictionary<string, Texture2D>();
        private Vector2 _scroll;
        private Vector2 _outerScroll;

        private static readonly HttpClient Http = new HttpClient();

        // ── Menu entry ────────────────────────────────────────────────────────────

        [MenuItem("LensError/YouTube Playlist Importer")]
        public static void Open()
        {
            var w = GetWindow<YoutubePlaylistImporter>("YT Playlist Importer");
            w.minSize = new Vector2(500, 420);
        }

        private void OnEnable()
        {
            _apiKey = EditorPrefs.GetString(PrefApiKey, "");
            ScanScenePlayers();
        }

        private void OnFocus() => ScanScenePlayers();

        private void OnDestroy()
        {
            foreach (var t in _thumbs.Values)
                if (t != null) DestroyImmediate(t);
        }

        // ── OnGUI ─────────────────────────────────────────────────────────────────

        private void OnGUI()
        {
            // Scrollable upper area: API key + settings + status + video list
            _outerScroll = EditorGUILayout.BeginScrollView(_outerScroll);
            DrawApiKey();
            EditorGUILayout.Space(6);
            DrawSettings();
            EditorGUILayout.Space(4);
            DrawStatus();
            if (_videos.Count > 0)
            {
                EditorGUILayout.Space(4);
                DrawList();
            }
            EditorGUILayout.EndScrollView();

            // Import button always visible at the bottom
            if (_videos.Count > 0)
            {
                EditorGUILayout.Space(2);
                DrawImportBtn();
            }
        }

        // ── UI sections ───────────────────────────────────────────────────────────

        private void DrawApiKey()
        {
            _apiKeyFoldout = EditorGUILayout.BeginFoldoutHeaderGroup(_apiKeyFoldout, "YouTube API Key");
            if (_apiKeyFoldout)
            {
                EditorGUI.indentLevel++;

                EditorGUILayout.BeginHorizontal();
                _apiKey = _showApiKey
                    ? EditorGUILayout.TextField("API Key", _apiKey)
                    : EditorGUILayout.PasswordField("API Key", _apiKey);
                if (GUILayout.Button(_showApiKey ? "Hide" : "Show", GUILayout.Width(46)))
                    _showApiKey = !_showApiKey;
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Save Key", GUILayout.Width(80)))
                {
                    EditorPrefs.SetString(PrefApiKey, _apiKey);
                    SetStatus("API key saved.", false);
                }
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.HelpBox(
                    "To get an API key: go to console.cloud.google.com, create a project, " +
                    "enable \"YouTube Data API v3\" under APIs & Services, then go to " +
                    "Credentials > Create Credentials > API Key. Paste it above.",
                    MessageType.None);

                EditorGUI.indentLevel--;
            }
            EditorGUILayout.EndFoldoutHeaderGroup();
        }

        private void DrawSettings()
        {
            EditorGUILayout.LabelField("Import Settings", EditorStyles.boldLabel);

            _playlistInput = EditorGUILayout.TextField("Playlist / Channel URL or ID", _playlistInput);

            // Scene player dropdown
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PrefixLabel("Scene Players");
            int newIdx = EditorGUILayout.Popup(_scenePlayerIdx, _scenePlayerOptions);
            if (newIdx != _scenePlayerIdx)
            {
                _scenePlayerIdx = newIdx;
                _targetPlayer = newIdx > 0 ? _scenePlayers[newIdx - 1] : null;
            }
            if (GUILayout.Button("Scan", GUILayout.Width(44)))
                ScanScenePlayers();
            EditorGUILayout.EndHorizontal();

            // ObjectField as drag-and-drop / cross-scene fallback; kept in sync with the dropdown
            EditorGUI.BeginChangeCheck();
            var picked = (CVRVideoPlayer)EditorGUILayout.ObjectField(
                "Target Video Player", _targetPlayer, typeof(CVRVideoPlayer), true);
            if (EditorGUI.EndChangeCheck())
            {
                _targetPlayer = picked;
                _scenePlayerIdx = 0;
                for (int i = 0; i < _scenePlayers.Length; i++)
                    if (_scenePlayers[i] == _targetPlayer) { _scenePlayerIdx = i + 1; break; }
            }

            _shuffleOnImport = EditorGUILayout.Toggle("Shuffle on Import", _shuffleOnImport);

            EditorGUI.BeginDisabledGroup(_shuffleOnImport);
            _sortOrder = (SortOrder)EditorGUILayout.EnumPopup("Sort Order", _sortOrder);
            EditorGUI.EndDisabledGroup();

            _appendToExisting = EditorGUILayout.Toggle("Append to Existing", _appendToExisting);

            EditorGUILayout.Space(2);

            bool ready = !string.IsNullOrWhiteSpace(_apiKey)
                && !string.IsNullOrWhiteSpace(_playlistInput)
                && _state != FetchState.Fetching;

            EditorGUI.BeginDisabledGroup(!ready);
            if (GUILayout.Button(
                _state == FetchState.Fetching ? "Fetching..." : "Fetch Playlist",
                GUILayout.Height(26)))
            {
                _ = FetchAsync();
            }
            EditorGUI.EndDisabledGroup();
        }

        private void DrawStatus()
        {
            if (string.IsNullOrEmpty(_status)) return;
            EditorGUILayout.HelpBox(_status,
                _state == FetchState.Error ? MessageType.Error : MessageType.Info);
        }

        private void DrawList()
        {
            EditorGUILayout.BeginHorizontal();
            string header = _meta != null
                ? string.Format("{0}  ({1} videos)", _meta.title, _videos.Count)
                : string.Format("{0} videos fetched", _videos.Count);
            EditorGUILayout.LabelField(header, EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("All", GUILayout.Width(34)))
                _videos.ForEach(v => { if (v.selectable) v.include = true; });
            if (GUILayout.Button("None", GUILayout.Width(42)))
                _videos.ForEach(v => v.include = false);
            EditorGUILayout.EndHorizontal();

            int sel = _videos.FindAll(v => v.include).Count;
            int selectable = _videos.FindAll(v => v.selectable).Count;
            EditorGUILayout.LabelField(
                string.Format("{0} of {1} selectable videos checked", sel, selectable),
                EditorStyles.miniLabel);

            _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.Height(300));
            foreach (var v in _videos)
                DrawEntry(v);
            EditorGUILayout.EndScrollView();
        }

        private void DrawEntry(VideoEntry v)
        {
            var prevBg = GUI.backgroundColor;
            switch (v.availability)
            {
                case VideoAvailability.Available:
                    GUI.backgroundColor = new Color(0.3f, 0.95f, 0.3f, 0.22f); break;
                case VideoAvailability.AgeRestricted:
                    GUI.backgroundColor = new Color(1f, 0.85f, 0.1f, 0.30f); break;
                default:
                    GUI.backgroundColor = new Color(0.95f, 0.3f, 0.3f, 0.22f); break;
            }

            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
            GUI.backgroundColor = prevBg;

            // Thumbnail
            Texture2D tex;
            if (_thumbs.TryGetValue(v.videoId, out tex) && tex != null)
                GUILayout.Label(tex, GUILayout.Width(80), GUILayout.Height(46));
            else
            {
                GUILayout.Box(GUIContent.none, GUILayout.Width(80), GUILayout.Height(46));
                if (!string.IsNullOrEmpty(v.thumbnailUrl) && !_thumbs.ContainsKey(v.videoId))
                    _ = LoadThumbAsync(v.videoId, v.thumbnailUrl, _fetchGen);
            }

            EditorGUILayout.BeginVertical();

            EditorGUILayout.BeginHorizontal();
            if (v.selectable)
                v.include = GUILayout.Toggle(v.include, GUIContent.none, GUILayout.Width(16));
            else
                GUILayout.Space(4);

            string badge;
            switch (v.availability)
            {
                case VideoAvailability.AgeRestricted: badge = "[AGE-RESTRICTED]  "; break;
                case VideoAvailability.Private:        badge = "[PRIVATE]  ";        break;
                case VideoAvailability.Deleted:        badge = "[DELETED VIDEO]";    break;
                case VideoAvailability.Unavailable:    badge = "[UNAVAILABLE]  ";    break;
                default:                               badge = "";                   break;
            }
            string displayTitle = v.availability == VideoAvailability.Deleted ? badge : badge + v.title;
            EditorGUILayout.LabelField(displayTitle, EditorStyles.boldLabel);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.LabelField("https://youtu.be/" + v.videoId, EditorStyles.miniLabel);
            EditorGUILayout.EndVertical();

            EditorGUILayout.EndHorizontal();
        }

        private void DrawImportBtn()
        {
            int count = _videos.FindAll(v => v.include).Count;
            bool can = _targetPlayer != null && count > 0;

            string label = _targetPlayer == null
                ? "Assign a Target Video Player above"
                : string.Format("Import {0} Video{1} into Player", count, count == 1 ? "" : "s");

            EditorGUI.BeginDisabledGroup(!can);
            if (GUILayout.Button(label, GUILayout.Height(32)))
                DoImport();
            EditorGUI.EndDisabledGroup();
        }

        // ── Core logic ────────────────────────────────────────────────────────────

        private async Task FetchAsync()
        {
            string id = ExtractPlaylistId(_playlistInput);

            bool isChannel = false;
            string chParam = null, chValue = null;
            if (string.IsNullOrEmpty(id))
            {
                if (!TryExtractChannelQuery(_playlistInput, out chParam, out chValue))
                {
                    SetStatus("Could not parse a playlist ID or channel URL from the input.", true);
                    return;
                }
                isChannel = true;
            }

            _state = FetchState.Fetching;
            _status = "";
            _videos.Clear();
            _meta = null;
            _fetchGen++;

            foreach (var t in _thumbs.Values)
                if (t != null) DestroyImmediate(t);
            _thumbs.Clear();

            Repaint();

            try
            {
                if (isChannel)
                {
                    _status = "Resolving channel...";
                    Repaint();
                    id = await GetChannelUploadsPlaylistIdAsync(chParam, chValue);
                }

                _meta = await GetPlaylistMetaAsync(id);
                var raw = await GetAllItemsAsync(id);
                var details = await GetVideoDetailsAsync(raw.ConvertAll(r => r.videoId));

                foreach (var r in raw)
                {
                    VideoDetail d;
                    details.TryGetValue(r.videoId, out d);
                    var avail = Classify(r.title, d);
                    _videos.Add(new VideoEntry
                    {
                        videoId      = r.videoId,
                        title        = string.IsNullOrEmpty(r.title) ? r.videoId : r.title,
                        thumbnailUrl = r.thumbnailUrl,
                        publishedAt  = r.publishedAt,
                        availability = avail,
                        selectable   = avail == VideoAvailability.Available
                                    || avail == VideoAvailability.AgeRestricted,
                        include      = avail == VideoAvailability.Available
                    });
                }

                int avCount   = _videos.FindAll(v => v.availability == VideoAvailability.Available).Count;
                int ageCount  = _videos.FindAll(v => v.availability == VideoAvailability.AgeRestricted).Count;
                int skipCount = _videos.Count - avCount - ageCount;

                string msg = string.Format("Fetched {0} videos: {1} available", _videos.Count, avCount);
                if (ageCount  > 0) msg += string.Format(", {0} age-restricted", ageCount);
                if (skipCount > 0) msg += string.Format(", {0} unavailable (excluded)", skipCount);

                SetStatus(msg, false);
                _state = FetchState.Done;
            }
            catch (Exception ex)
            {
                SetStatus("Fetch failed: " + ex.Message, true);
            }

            Repaint();
        }

        private void DoImport()
        {
            var list = _videos.FindAll(v => v.include);
            if (list.Count == 0) return;

            // Apply sort order; shuffle overrides
            if (_shuffleOnImport)
                Shuffle(list);
            else if (_sortOrder == SortOrder.NewestFirst)
                list.Sort((a, b) => DateTime.Compare(b.publishedAt, a.publishedAt));
            else if (_sortOrder == SortOrder.OldestFirst)
                list.Sort((a, b) => DateTime.Compare(a.publishedAt, b.publishedAt));

            Undo.RecordObject(_targetPlayer, "YouTube Playlist Import");

            string baseTitle = _meta != null ? _meta.title : "YouTube Playlist";
            string thumbUrl  = _meta != null ? _meta.thumbnailUrl : "";

            var matching = _targetPlayer.entities.FindAll(p => IsMatchingTitle(p.playlistTitle, baseTitle));

            if (matching.Count > 0)
            {
                // MERGE — deduplicate, fill existing playlists, overflow into new numbered ones
                var existingIds = new HashSet<string>();
                foreach (var p in matching)
                    foreach (var e in p.playlistVideos)
                    {
                        string vid = ExtractVideoId(e.videoUrl);
                        if (!string.IsNullOrEmpty(vid)) existingIds.Add(vid);
                    }

                var newItems = list.FindAll(v => !existingIds.Contains(v.videoId));
                int added = 0;

                if (newItems.Count > 0)
                {
                    int itemIdx = 0;

                    foreach (var p in matching)
                    {
                        while (p.playlistVideos.Count < ChunkSize && itemIdx < newItems.Count)
                        {
                            p.playlistVideos.Add(MakeEntity(newItems[itemIdx++]));
                            added++;
                        }
                        if (itemIdx >= newItems.Count) break;
                    }

                    int nextNum = MaxPlaylistNum(matching, baseTitle) + 1;
                    while (itemIdx < newItems.Count)
                    {
                        var overflow = new CVRVideoPlayerPlaylist
                        {
                            playlistTitle        = string.Format("{0} ({1})", baseTitle, nextNum++),
                            playlistThumbnailUrl = thumbUrl
                        };
                        while (overflow.playlistVideos.Count < ChunkSize && itemIdx < newItems.Count)
                        {
                            overflow.playlistVideos.Add(MakeEntity(newItems[itemIdx++]));
                            added++;
                        }
                        _targetPlayer.entities.Add(overflow);
                        matching.Add(overflow);
                    }
                }

                if (_shuffleOnImport)
                    foreach (var p in matching) Shuffle(p.playlistVideos);

                EditorUtility.SetDirty(_targetPlayer);

                string mergeMsg = newItems.Count > 0
                    ? string.Format("Added {0} new video{1} to \"{2}\".", added, added == 1 ? "" : "s", baseTitle)
                    : string.Format("No new videos -- \"{0}\" is already up to date.", baseTitle);
                if (_shuffleOnImport) mergeMsg += " Reshuffled.";
                SetStatus(mergeMsg, false);
            }
            else
            {
                // NEW — split into ChunkSize chunks if needed
                if (!_appendToExisting)
                    _targetPlayer.entities.Clear();

                bool multi = list.Count > ChunkSize;
                int chunkNum = 0;
                for (int i = 0; i < list.Count; i += ChunkSize)
                {
                    chunkNum++;
                    var chunk = list.GetRange(i, Math.Min(ChunkSize, list.Count - i));
                    string title = multi
                        ? string.Format("{0} ({1})", baseTitle, chunkNum)
                        : baseTitle;

                    var playlist = new CVRVideoPlayerPlaylist
                    {
                        playlistTitle        = title,
                        playlistThumbnailUrl = thumbUrl
                    };
                    foreach (var v in chunk)
                        playlist.playlistVideos.Add(MakeEntity(v));
                    _targetPlayer.entities.Add(playlist);
                }

                EditorUtility.SetDirty(_targetPlayer);

                string importMsg = chunkNum > 1
                    ? string.Format("Imported {0} videos across {1} playlists into {2}.", list.Count, chunkNum, _targetPlayer.name)
                    : string.Format("Imported {0} video{1} into {2}.", list.Count, list.Count == 1 ? "" : "s", _targetPlayer.name);
                SetStatus(importMsg, false);
            }
        }

        private static CVRVideoPlayerPlaylistEntity MakeEntity(VideoEntry v) =>
            new CVRVideoPlayerPlaylistEntity
            {
                videoTitle            = v.title,
                videoUrl              = "https://www.youtube.com/watch?v=" + v.videoId,
                thumbnailUrl          = v.thumbnailUrl,
                introEndInSeconds     = 0,
                creditsStartInSeconds = 0
            };

        private static bool IsMatchingTitle(string playlistTitle, string baseTitle)
        {
            if (playlistTitle == baseTitle) return true;
            string prefix = baseTitle + " (";
            if (!playlistTitle.StartsWith(prefix) || !playlistTitle.EndsWith(")")) return false;
            string inner = playlistTitle.Substring(prefix.Length, playlistTitle.Length - prefix.Length - 1);
            int n;
            return int.TryParse(inner, out n) && n > 0;
        }

        private static int MaxPlaylistNum(List<CVRVideoPlayerPlaylist> playlists, string baseTitle)
        {
            int max = 0;
            foreach (var p in playlists)
            {
                if (p.playlistTitle == baseTitle) { max = Math.Max(max, 1); continue; }
                string prefix = baseTitle + " (";
                if (!p.playlistTitle.StartsWith(prefix) || !p.playlistTitle.EndsWith(")")) continue;
                string inner = p.playlistTitle.Substring(prefix.Length, p.playlistTitle.Length - prefix.Length - 1);
                int n;
                if (int.TryParse(inner, out n)) max = Math.Max(max, n);
            }
            return max;
        }

        // ── API calls ─────────────────────────────────────────────────────────────

        private async Task<string> GetChannelUploadsPlaylistIdAsync(string param, string value)
        {
            string url = YtApi + "channels?part=contentDetails&" + param + "="
                + Uri.EscapeDataString(value) + "&key=" + _apiKey;
            string json = await GetAsync(url);
            var r = JsonUtility.FromJson<YtChannelListResp>(json);
            if (r == null || r.items == null || r.items.Length == 0)
                throw new Exception("Channel not found.");
            string uploads = r.items[0].contentDetails.relatedPlaylists.uploads;
            if (string.IsNullOrEmpty(uploads))
                throw new Exception("Could not find uploads playlist for this channel.");
            return uploads;
        }

        private async Task<PlaylistMeta> GetPlaylistMetaAsync(string playlistId)
        {
            string json = await GetAsync(
                YtApi + "playlists?part=snippet&id=" + playlistId + "&key=" + _apiKey);
            var r = JsonUtility.FromJson<YtPlaylistListResp>(json);
            if (r == null || r.items == null || r.items.Length == 0)
                throw new Exception("Playlist not found or is private.");
            var s = r.items[0].snippet;
            return new PlaylistMeta { title = s.title, thumbnailUrl = BestThumb(s.thumbnails) };
        }

        private async Task<List<RawItem>> GetAllItemsAsync(string playlistId)
        {
            var all = new List<RawItem>();
            string pageToken = null;

            do
            {
                string url = YtApi
                    + "playlistItems?part=snippet,contentDetails&playlistId=" + playlistId
                    + "&maxResults=50&key=" + _apiKey;
                if (!string.IsNullOrEmpty(pageToken))
                    url += "&pageToken=" + pageToken;

                string json = await GetAsync(url);
                var r = JsonUtility.FromJson<YtPlaylistItemListResp>(json);

                if (r != null && r.items != null)
                    foreach (var item in r.items)
                        all.Add(new RawItem
                        {
                            videoId      = item.contentDetails.videoId,
                            title        = item.snippet.title,
                            thumbnailUrl = BestThumb(item.snippet.thumbnails),
                            publishedAt  = ParseDate(item.contentDetails.videoPublishedAt)
                        });

                pageToken = r != null ? r.nextPageToken : null;
            }
            while (!string.IsNullOrEmpty(pageToken));

            return all;
        }

        private async Task<Dictionary<string, VideoDetail>> GetVideoDetailsAsync(List<string> ids)
        {
            var map = new Dictionary<string, VideoDetail>();

            for (int i = 0; i < ids.Count; i += 50)
            {
                var batch = ids.GetRange(i, Math.Min(50, ids.Count - i));
                string url = YtApi
                    + "videos?part=status,contentDetails&id="
                    + string.Join(",", batch)
                    + "&key=" + _apiKey;

                string json = await GetAsync(url);
                var r = JsonUtility.FromJson<YtVideoListResp>(json);

                if (r != null && r.items != null)
                    foreach (var v in r.items)
                        map[v.id] = new VideoDetail
                        {
                            privacyStatus = v.status != null ? v.status.privacyStatus : "",
                            ytRating = (v.contentDetails != null && v.contentDetails.contentRating != null)
                                ? v.contentDetails.contentRating.ytRating : ""
                        };
            }

            return map;
        }

        private async Task LoadThumbAsync(string id, string url, int gen)
        {
            _thumbs[id] = null;
            try
            {
                byte[] data = await Http.GetByteArrayAsync(url);
                int capturedGen = gen;
                EditorApplication.delayCall += () =>
                {
                    if (capturedGen != _fetchGen) return;
                    var tex = new Texture2D(2, 2, TextureFormat.RGB24, false);
                    if (tex.LoadImage(data))
                    {
                        _thumbs[id] = tex;
                        Repaint();
                    }
                };
            }
            catch
            {
                _thumbs.Remove(id);
            }
        }

        private async Task<string> GetAsync(string url)
        {
            var resp = await Http.GetAsync(url);
            string body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                string snippet = body.Length > 300 ? body.Substring(0, 300) : body;
                throw new Exception(string.Format("HTTP {0}: {1}", (int)resp.StatusCode, snippet));
            }
            return body;
        }

        // ── Scene scanning ────────────────────────────────────────────────────────

        private void ScanScenePlayers()
        {
            _scenePlayers = FindObjectsOfType<CVRVideoPlayer>();
            var opts = new string[_scenePlayers.Length + 1];
            opts[0] = "None";
            for (int i = 0; i < _scenePlayers.Length; i++)
                opts[i + 1] = _scenePlayers[i].name;
            _scenePlayerOptions = opts;

            _scenePlayerIdx = 0;
            for (int i = 0; i < _scenePlayers.Length; i++)
                if (_scenePlayers[i] == _targetPlayer) { _scenePlayerIdx = i + 1; break; }
        }

        // ── Helpers ───────────────────────────────────────────────────────────────

        private static VideoAvailability Classify(string title, VideoDetail d)
        {
            if (title == "Deleted video") return VideoAvailability.Deleted;
            if (title == "Private video") return VideoAvailability.Private;
            if (d == null) return VideoAvailability.Unavailable;
            if (d.privacyStatus == "private") return VideoAvailability.Private;
            if (d.ytRating == "ytAgeRestricted") return VideoAvailability.AgeRestricted;
            return VideoAvailability.Available;
        }

        private static string BestThumb(YtThumbnailSet t)
        {
            if (t == null) return "";
            if (t.maxres   != null && !string.IsNullOrEmpty(t.maxres.url))   return t.maxres.url;
            if (t.standard != null && !string.IsNullOrEmpty(t.standard.url)) return t.standard.url;
            if (t.high     != null && !string.IsNullOrEmpty(t.high.url))     return t.high.url;
            if (t.medium   != null && !string.IsNullOrEmpty(t.medium.url))   return t.medium.url;
            return "";
        }

        // Accepts playlist URLs/IDs. Returns null for channel URLs (caller tries TryExtractChannelQuery).
        private static string ExtractPlaylistId(string s)
        {
            if (s == null) return null;
            s = s.Trim();
            if (string.IsNullOrEmpty(s)) return null;

            if (!s.Contains("://"))
            {
                // Bare @handle or UC... channel ID -- not a playlist
                if (s.StartsWith("@")) return null;
                if (s.StartsWith("UC") && !s.Contains(" ") && s.Length > 10) return null;
                return s;
            }

            try
            {
                string query = new Uri(s).Query.TrimStart('?');
                foreach (var seg in query.Split('&'))
                {
                    var kv = seg.Split(new[] { '=' }, 2);
                    if (kv.Length == 2 && kv[0] == "list")
                        return Uri.UnescapeDataString(kv[1]);
                }
            }
            catch { }
            return null;
        }

        // Returns true when the input looks like a channel (not a playlist).
        private static bool TryExtractChannelQuery(string s, out string param, out string value)
        {
            param = value = null;
            if (string.IsNullOrWhiteSpace(s)) return false;
            s = s.Trim();

            // Bare @handle
            if (s.StartsWith("@") && !s.Contains("/"))
            {
                param = "forHandle"; value = s.Substring(1); return true;
            }
            // Bare UC... channel ID (no spaces, no slashes)
            if (!s.Contains("/") && !s.Contains(" ") && s.StartsWith("UC") && s.Length > 10)
            {
                param = "id"; value = s; return true;
            }
            if (!s.Contains("://")) return false;

            try
            {
                var uri   = new Uri(s);
                var parts = uri.AbsolutePath.TrimStart('/').Split('/');

                // youtube.com/@handle
                if (parts.Length > 0 && parts[0].StartsWith("@"))
                {
                    param = "forHandle"; value = parts[0].Substring(1); return true;
                }
                // youtube.com/channel/UCxxx
                if (parts.Length >= 2 && parts[0] == "channel")
                {
                    param = "id"; value = parts[1]; return true;
                }
                // youtube.com/user/username
                if (parts.Length >= 2 && parts[0] == "user")
                {
                    param = "forUsername"; value = parts[1]; return true;
                }
                // youtube.com/c/channelName (legacy)
                if (parts.Length >= 2 && parts[0] == "c")
                {
                    param = "forHandle"; value = parts[1]; return true;
                }
            }
            catch { }
            return false;
        }

        private static string ExtractVideoId(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            try
            {
                var uri = new Uri(url);
                if (uri.Host == "youtu.be" || uri.Host == "www.youtu.be")
                    return uri.AbsolutePath.TrimStart('/');
                foreach (var seg in uri.Query.TrimStart('?').Split('&'))
                {
                    var kv = seg.Split(new[] { '=' }, 2);
                    if (kv.Length == 2 && kv[0] == "v")
                        return Uri.UnescapeDataString(kv[1]);
                }
            }
            catch { }
            return null;
        }

        private static DateTime ParseDate(string s)
        {
            DateTime dt;
            if (!string.IsNullOrEmpty(s) &&
                DateTime.TryParse(s, null, DateTimeStyles.RoundtripKind, out dt))
                return dt;
            return DateTime.MinValue;
        }

        private void SetStatus(string msg, bool error)
        {
            _status = msg;
            if (_state != FetchState.Fetching)
                _state = error ? FetchState.Error : FetchState.Done;
        }

        private static void Shuffle<T>(List<T> list)
        {
            var rng = new System.Random();
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                T tmp = list[i]; list[i] = list[j]; list[j] = tmp;
            }
        }

        // ── Plain data types ──────────────────────────────────────────────────────

        private enum VideoAvailability { Available, AgeRestricted, Private, Deleted, Unavailable }
        private enum SortOrder         { PlaylistOrder, NewestFirst, OldestFirst }

        private class VideoEntry
        {
            public string videoId, title, thumbnailUrl;
            public DateTime publishedAt;
            public VideoAvailability availability;
            public bool selectable, include;
        }

        private class PlaylistMeta { public string title, thumbnailUrl; }
        private class RawItem      { public string videoId, title, thumbnailUrl; public DateTime publishedAt; }
        private class VideoDetail  { public string privacyStatus, ytRating; }

        // ── JSON models (JsonUtility) ─────────────────────────────────────────────

        [Serializable] private class YtPlaylistListResp     { public YtPLItem[]  items; }
        [Serializable] private class YtPLItem               { public YtPLSnippet snippet; }
        [Serializable] private class YtPLSnippet            { public string title; public YtThumbnailSet thumbnails; }

        [Serializable] private class YtPlaylistItemListResp { public string nextPageToken; public YtPIItem[] items; }
        [Serializable] private class YtPIItem               { public YtPISnippet snippet; public YtPICD contentDetails; }
        [Serializable] private class YtPISnippet            { public string title; public YtThumbnailSet thumbnails; }
        [Serializable] private class YtPICD                 { public string videoId; public string videoPublishedAt; }

        [Serializable] private class YtVideoListResp        { public YtVItem[]   items; }
        [Serializable] private class YtVItem                { public string id; public YtVStatus status; public YtVCD contentDetails; }
        [Serializable] private class YtVStatus              { public string privacyStatus; }
        [Serializable] private class YtVCD                  { public YtContentRating contentRating; }
        [Serializable] private class YtContentRating        { public string ytRating; }

        [Serializable] private class YtChannelListResp      { public YtChItem[]  items; }
        [Serializable] private class YtChItem               { public YtChCD      contentDetails; }
        [Serializable] private class YtChCD                 { public YtChRelated relatedPlaylists; }
        [Serializable] private class YtChRelated            { public string uploads; }

        [Serializable]
        private class YtThumbnailSet
        {
            public YtThumb medium;
            public YtThumb high;
            public YtThumb standard;
            public YtThumb maxres;
        }

        [Serializable]
        private class YtThumb { public string url; }
    }
}
#endif
