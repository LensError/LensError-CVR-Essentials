using System;
using System.Collections.Generic;
using ABI.CCK.Components;
using CVR.CCKEditor.ContentBuilder;
using UnityEngine;

namespace LensError
{
    [AddComponentMenu("LensError/YouTube Playlist Sync")]
    [DisallowMultipleComponent]
    public class YouTubePlaylistSync : MonoBehaviour, ICCKEditorOnly
    {
        public enum PlaylistSortOrder { PlaylistOrder, NewestFirst, OldestFirst }

        [Serializable]
        public class SyncEntry
        {
            public string playlistUrl = "";
            public bool shuffle;
            public bool includeAgeRestricted;
            public PlaylistSortOrder sortOrder;
            // Written by the custom editor after each successful sync
            [HideInInspector] public string lastSyncedUtc = "";
            [HideInInspector] public int    lastSyncedCount;
        }

        // If null the editor resolves the CVRVideoPlayer from this same GameObject
        public CVRVideoPlayer targetOverride;

        public List<SyncEntry> playlists = new List<SyncEntry>();
    }
}
