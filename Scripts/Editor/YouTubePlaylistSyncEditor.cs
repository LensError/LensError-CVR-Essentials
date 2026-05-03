#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using ABI.CCK.Components;
using LensError;
using UnityEditor;
using UnityEngine;

namespace LensError.Editor
{
    [CustomEditor(typeof(YouTubePlaylistSync))]
    public class YouTubePlaylistSyncEditor : UnityEditor.Editor
    {
        private const string PrefApiKey = "LensError_YtImporter_ApiKey";
        private const string YtApi      = "https://www.googleapis.com/youtube/v3/";
        private const int    ChunkSize  = 200;

        private static readonly HttpClient Http = new HttpClient();

        // Per-inspector fetch state (resets when Inspector closes)
        private bool   _isFetching;
        private int    _fetchingIndex = -1;
        private string _status        = "";
        private bool   _statusIsError;
        private bool   _showApiKey;
        private bool   _apiKeyFoldout = true;

        // ── Inspector ─────────────────────────────────────────────────────────────

        public override void OnInspectorGUI()
        {
            var sync   = (YouTubePlaylistSync)target;
            var player = ResolvePlayer(sync);

            DrawApiKey();
            // Re-read after DrawApiKey in case the user just saved a new key
            string apiKey = EditorPrefs.GetString(PrefApiKey, "");

            DrawPlayerWarning(player);

            EditorGUILayout.Space(4);

            // Target override field
            EditorGUI.BeginChangeCheck();
            var picked = (CVRVideoPlayer)EditorGUILayout.ObjectField(
                "Target Override", sync.targetOverride, typeof(CVRVideoPlayer), true);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(sync, "Set Target Override");
                sync.targetOverride = picked;
                EditorUtility.SetDirty(sync);
                player = ResolvePlayer(sync);
            }

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Managed Playlists", EditorStyles.boldLabel);

            // Draw entries; collect any removal request
            int removeAt = -1;
            for (int i = 0; i < sync.playlists.Count; i++)
            {
                if (DrawEntry(sync, player, apiKey, i))
                    removeAt = i;
            }
            if (removeAt >= 0)
            {
                Undo.RecordObject(sync, "Remove Playlist Entry");
                sync.playlists.RemoveAt(removeAt);
                EditorUtility.SetDirty(sync);
            }

            EditorGUILayout.Space(4);
            if (GUILayout.Button("+ Add Playlist", GUILayout.Height(24)))
            {
                Undo.RecordObject(sync, "Add Playlist Entry");
                sync.playlists.Add(new YouTubePlaylistSync.SyncEntry());
                EditorUtility.SetDirty(sync);
            }

            EditorGUILayout.Space(4);

            // Sync All
            bool canSyncAll = !_isFetching && player != null
                && !string.IsNullOrEmpty(apiKey) && sync.playlists.Count > 0;
            EditorGUI.BeginDisabledGroup(!canSyncAll);
            if (GUILayout.Button(
                _isFetching && _fetchingIndex == -1 ? "Syncing..." : "Sync All Playlists",
                GUILayout.Height(30)))
            {
                _ = SyncAllAsync(sync, player, apiKey);
            }
            EditorGUI.EndDisabledGroup();

            if (!string.IsNullOrEmpty(_status))
            {
                EditorGUILayout.Space(2);
                EditorGUILayout.HelpBox(_status,
                    _statusIsError ? MessageType.Error : MessageType.Info);
            }
        }

        private void DrawApiKey()
        {
            string current = EditorPrefs.GetString(PrefApiKey, "");
            string label   = string.IsNullOrEmpty(current) ? "YouTube API Key  (not set)" : "YouTube API Key";

            _apiKeyFoldout = EditorGUILayout.BeginFoldoutHeaderGroup(_apiKeyFoldout, label);
            if (_apiKeyFoldout)
            {
                EditorGUI.indentLevel++;

                EditorGUILayout.BeginHorizontal();
                string edited = _showApiKey
                    ? EditorGUILayout.TextField("API Key", current)
                    : EditorGUILayout.PasswordField("API Key", current);
                if (GUILayout.Button(_showApiKey ? "Hide" : "Show", GUILayout.Width(46)))
                    _showApiKey = !_showApiKey;
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                EditorGUI.BeginDisabledGroup(edited == current);
                if (GUILayout.Button("Save Key", GUILayout.Width(80)))
                    EditorPrefs.SetString(PrefApiKey, edited);
                EditorGUI.EndDisabledGroup();
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.HelpBox(
                    "To get a key: console.cloud.google.com > create project > enable " +
                    "\"YouTube Data API v3\" > Credentials > Create Credentials > API Key.",
                    MessageType.None);

                EditorGUI.indentLevel--;
            }
            EditorGUILayout.EndFoldoutHeaderGroup();
        }

        private void DrawPlayerWarning(CVRVideoPlayer player)
        {
            if (player == null)
                EditorGUILayout.HelpBox(
                    "No CVRVideoPlayer found. Attach this to the same GameObject as a CVRVideoPlayer, " +
                    "or assign one to Target Override.",
                    MessageType.Warning);
        }

        // Returns true if the Remove button was clicked for entry i
        private bool DrawEntry(YouTubePlaylistSync sync, CVRVideoPlayer player, string apiKey, int i)
        {
            var  entry         = sync.playlists[i];
            bool isFetchingThis = _isFetching && _fetchingIndex == i;
            bool removeClicked  = false;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            // Title row
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(
                string.Format("Playlist {0}", i + 1), EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            if (!string.IsNullOrEmpty(entry.lastSyncedUtc))
                EditorGUILayout.LabelField(
                    string.Format("synced {0}", entry.lastSyncedUtc),
                    EditorStyles.miniLabel);
            EditorGUILayout.EndHorizontal();

            // URL
            EditorGUI.BeginChangeCheck();
            string newUrl = EditorGUILayout.TextField("URL or ID", entry.playlistUrl);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(sync, "Edit Playlist URL");
                entry.playlistUrl = newUrl;
                EditorUtility.SetDirty(sync);
            }

            // Shuffle
            EditorGUI.BeginChangeCheck();
            bool newShuffle = EditorGUILayout.Toggle("Shuffle on Sync", entry.shuffle);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(sync, "Toggle Shuffle");
                entry.shuffle = newShuffle;
                EditorUtility.SetDirty(sync);
            }

            // Age-restricted
            EditorGUI.BeginChangeCheck();
            bool newAgeRestricted = EditorGUILayout.Toggle("Include Age-Restricted", entry.includeAgeRestricted);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(sync, "Toggle Age-Restricted");
                entry.includeAgeRestricted = newAgeRestricted;
                EditorUtility.SetDirty(sync);
            }

            // Last synced detail
            if (entry.lastSyncedCount > 0)
                EditorGUILayout.LabelField(
                    string.Format("{0} videos in last sync", entry.lastSyncedCount),
                    EditorStyles.miniLabel);

            // Bottom button row
            EditorGUILayout.BeginHorizontal();
            bool canSync = !_isFetching && player != null
                && !string.IsNullOrEmpty(apiKey)
                && !string.IsNullOrEmpty(entry.playlistUrl);
            EditorGUI.BeginDisabledGroup(!canSync);
            if (GUILayout.Button(
                isFetchingThis ? "Syncing..." : "Sync Now",
                GUILayout.Height(22)))
            {
                _ = SyncEntryAsync(sync, player, apiKey, i);
            }
            EditorGUI.EndDisabledGroup();
            if (GUILayout.Button("Remove", GUILayout.Width(62), GUILayout.Height(22)))
                removeClicked = true;
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(2);

            return removeClicked;
        }

        // ── Sync logic ────────────────────────────────────────────────────────────

        private async Task SyncEntryAsync(
            YouTubePlaylistSync sync, CVRVideoPlayer player, string apiKey, int index)
        {
            var entry = sync.playlists[index];
            _isFetching    = true;
            _fetchingIndex = index;
            _status        = "";
            _statusIsError = false;
            Repaint();

            try
            {
                string id = ExtractPlaylistId(entry.playlistUrl);
                if (string.IsNullOrEmpty(id))
                    throw new Exception("Could not parse a playlist ID from the URL.");

                var meta    = await GetPlaylistMetaAsync(id, apiKey);
                var raw     = await GetAllItemsAsync(id, apiKey);
                var details = await GetVideoDetailsAsync(raw.ConvertAll(r => r.videoId), apiKey);

                var items = BuildImportList(raw, details, entry.includeAgeRestricted);
                if (entry.shuffle) Shuffle(items);

                Undo.RecordObject(player, "YouTube Playlist Sync");
                string resultMsg = ApplyToPlayer(player, meta, items, entry.shuffle);
                EditorUtility.SetDirty(player);

                Undo.RecordObject(sync, "Update Sync Timestamp");
                entry.lastSyncedUtc   = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm") + " UTC";
                entry.lastSyncedCount = items.Count;
                EditorUtility.SetDirty(sync);

                _status = string.Format("\"{0}\" synced. {1}", meta.title, resultMsg);
            }
            catch (Exception ex)
            {
                _status        = "Sync failed: " + ex.Message;
                _statusIsError = true;
            }

            _isFetching    = false;
            _fetchingIndex = -1;
            Repaint();
        }

        private async Task SyncAllAsync(
            YouTubePlaylistSync sync, CVRVideoPlayer player, string apiKey)
        {
            _isFetching    = true;
            _fetchingIndex = -1;
            _status        = "";
            _statusIsError = false;

            int synced = 0;
            for (int i = 0; i < sync.playlists.Count; i++)
            {
                var entry = sync.playlists[i];
                if (string.IsNullOrEmpty(entry.playlistUrl)) continue;

                _fetchingIndex = i;
                Repaint();

                try
                {
                    string id = ExtractPlaylistId(entry.playlistUrl);
                    if (string.IsNullOrEmpty(id)) continue;

                    var meta    = await GetPlaylistMetaAsync(id, apiKey);
                    var raw     = await GetAllItemsAsync(id, apiKey);
                    var details = await GetVideoDetailsAsync(raw.ConvertAll(r => r.videoId), apiKey);

                    var items = BuildImportList(raw, details, entry.includeAgeRestricted);
                    if (entry.shuffle) Shuffle(items);

                    Undo.RecordObject(player, "YouTube Playlist Sync");
                    ApplyToPlayer(player, meta, items, entry.shuffle);
                    EditorUtility.SetDirty(player);

                    Undo.RecordObject(sync, "Update Sync Timestamp");
                    entry.lastSyncedUtc   = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm") + " UTC";
                    entry.lastSyncedCount = items.Count;
                    EditorUtility.SetDirty(sync);

                    synced++;
                }
                catch (Exception ex)
                {
                    _status        = string.Format("Playlist {0} failed: {1}", i + 1, ex.Message);
                    _statusIsError = true;
                    break;
                }
            }

            _isFetching    = false;
            _fetchingIndex = -1;
            if (!_statusIsError)
                _status = string.Format(
                    "Sync All complete: {0} of {1} playlists synced.",
                    synced, sync.playlists.Count);
            Repaint();
        }

        // Returns a short human-readable result message (what was added/updated)
        private string ApplyToPlayer(
            CVRVideoPlayer player, PlaylistMeta meta,
            List<ImportEntry> list, bool shuffle)
        {
            string baseTitle = meta.title;
            string thumbUrl  = meta.thumbnailUrl;

            var matching = player.entities.FindAll(
                p => IsMatchingTitle(p.playlistTitle, baseTitle));

            if (matching.Count > 0)
            {
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
                    int idx = 0;
                    foreach (var p in matching)
                    {
                        while (p.playlistVideos.Count < ChunkSize && idx < newItems.Count)
                        {
                            p.playlistVideos.Add(MakeEntity(newItems[idx++]));
                            added++;
                        }
                        if (idx >= newItems.Count) break;
                    }

                    int nextNum = MaxPlaylistNum(matching, baseTitle) + 1;
                    while (idx < newItems.Count)
                    {
                        var overflow = new CVRVideoPlayerPlaylist
                        {
                            playlistTitle        = string.Format("{0} ({1})", baseTitle, nextNum++),
                            playlistThumbnailUrl = thumbUrl
                        };
                        while (overflow.playlistVideos.Count < ChunkSize && idx < newItems.Count)
                        {
                            overflow.playlistVideos.Add(MakeEntity(newItems[idx++]));
                            added++;
                        }
                        player.entities.Add(overflow);
                        matching.Add(overflow);
                    }
                }

                if (shuffle)
                    foreach (var p in matching) Shuffle(p.playlistVideos);

                if (newItems.Count == 0)
                    return "Already up to date.";
                return string.Format("{0} new video{1} added.", added, added == 1 ? "" : "s");
            }
            else
            {
                bool multi    = list.Count > ChunkSize;
                int chunkNum  = 0;
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
                    player.entities.Add(playlist);
                }

                string countMsg = chunkNum > 1
                    ? string.Format("{0} videos across {1} playlist chunks.", list.Count, chunkNum)
                    : string.Format("{0} video{1} imported.", list.Count, list.Count == 1 ? "" : "s");
                return countMsg;
            }
        }

        // ── API calls ─────────────────────────────────────────────────────────────

        private async Task<PlaylistMeta> GetPlaylistMetaAsync(string id, string key)
        {
            string json = await GetAsync(
                YtApi + "playlists?part=snippet&id=" + id + "&key=" + key);
            var r = JsonUtility.FromJson<YtPlaylistListResp>(json);
            if (r == null || r.items == null || r.items.Length == 0)
                throw new Exception("Playlist not found or is private.");
            var s = r.items[0].snippet;
            return new PlaylistMeta { title = s.title, thumbnailUrl = BestThumb(s.thumbnails) };
        }

        private async Task<List<RawItem>> GetAllItemsAsync(string id, string key)
        {
            var all = new List<RawItem>();
            string pageToken = null;
            do
            {
                string url = YtApi
                    + "playlistItems?part=snippet,contentDetails&playlistId=" + id
                    + "&maxResults=50&key=" + key;
                if (!string.IsNullOrEmpty(pageToken)) url += "&pageToken=" + pageToken;

                string json = await GetAsync(url);
                var r = JsonUtility.FromJson<YtPlaylistItemListResp>(json);
                if (r != null && r.items != null)
                    foreach (var item in r.items)
                        all.Add(new RawItem
                        {
                            videoId      = item.contentDetails.videoId,
                            title        = item.snippet.title,
                            thumbnailUrl = BestThumb(item.snippet.thumbnails)
                        });
                pageToken = r != null ? r.nextPageToken : null;
            }
            while (!string.IsNullOrEmpty(pageToken));
            return all;
        }

        private async Task<Dictionary<string, VideoDetail>> GetVideoDetailsAsync(
            List<string> ids, string key)
        {
            var map = new Dictionary<string, VideoDetail>();
            for (int i = 0; i < ids.Count; i += 50)
            {
                var batch = ids.GetRange(i, Math.Min(50, ids.Count - i));
                string url = YtApi
                    + "videos?part=status,contentDetails&id="
                    + string.Join(",", batch) + "&key=" + key;
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

        private async Task<string> GetAsync(string url)
        {
            var resp = await Http.GetAsync(url);
            string body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                string snip = body.Length > 300 ? body.Substring(0, 300) : body;
                throw new Exception(string.Format("HTTP {0}: {1}", (int)resp.StatusCode, snip));
            }
            return body;
        }

        // ── Helpers ───────────────────────────────────────────────────────────────

        private static CVRVideoPlayer ResolvePlayer(YouTubePlaylistSync sync)
        {
            return sync.targetOverride != null
                ? sync.targetOverride
                : sync.GetComponent<CVRVideoPlayer>();
        }

        private static List<ImportEntry> BuildImportList(
            List<RawItem> raw, Dictionary<string, VideoDetail> details,
            bool includeAgeRestricted)
        {
            var list = new List<ImportEntry>();
            foreach (var r in raw)
            {
                VideoDetail d;
                details.TryGetValue(r.videoId, out d);

                bool deleted  = r.title == "Deleted video";
                bool priv     = r.title == "Private video"
                             || (d != null && d.privacyStatus == "private");
                bool ageGated = d != null && d.ytRating == "ytAgeRestricted";
                bool unavail  = d == null && !deleted && !priv;

                if (deleted || priv || unavail) continue;
                if (ageGated && !includeAgeRestricted) continue;

                list.Add(new ImportEntry
                {
                    videoId      = r.videoId,
                    title        = string.IsNullOrEmpty(r.title) ? r.videoId : r.title,
                    thumbnailUrl = r.thumbnailUrl
                });
            }
            return list;
        }

        private static CVRVideoPlayerPlaylistEntity MakeEntity(ImportEntry v) =>
            new CVRVideoPlayerPlaylistEntity
            {
                videoTitle           = v.title,
                videoUrl             = "https://www.youtube.com/watch?v=" + v.videoId,
                thumbnailUrl         = v.thumbnailUrl,
                introEndInSeconds    = 0,
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

        private static string ExtractPlaylistId(string s)
        {
            if (s == null) return null;
            s = s.Trim();
            if (string.IsNullOrEmpty(s)) return null;
            if (!s.Contains("://")) return s;
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
                    if (kv.Length == 2 && kv[0] == "v") return Uri.UnescapeDataString(kv[1]);
                }
            }
            catch { }
            return null;
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

        private class PlaylistMeta  { public string title, thumbnailUrl; }
        private class RawItem       { public string videoId, title, thumbnailUrl; }
        private class VideoDetail   { public string privacyStatus, ytRating; }
        private class ImportEntry   { public string videoId, title, thumbnailUrl; }

        // ── JSON models ───────────────────────────────────────────────────────────

        [Serializable] private class YtPlaylistListResp     { public YtPLItem[]  items; }
        [Serializable] private class YtPLItem               { public YtPLSnippet snippet; }
        [Serializable] private class YtPLSnippet            { public string title; public YtThumbnailSet thumbnails; }

        [Serializable] private class YtPlaylistItemListResp { public string nextPageToken; public YtPIItem[] items; }
        [Serializable] private class YtPIItem               { public YtPISnippet snippet; public YtPICD contentDetails; }
        [Serializable] private class YtPISnippet            { public string title; public YtThumbnailSet thumbnails; }
        [Serializable] private class YtPICD                 { public string videoId; }

        [Serializable] private class YtVideoListResp        { public YtVItem[]   items; }
        [Serializable] private class YtVItem                { public string id; public YtVStatus status; public YtVCD contentDetails; }
        [Serializable] private class YtVStatus              { public string privacyStatus; }
        [Serializable] private class YtVCD                  { public YtContentRating contentRating; }
        [Serializable] private class YtContentRating        { public string ytRating; }

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
