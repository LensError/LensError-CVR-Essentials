using System;
using System.Collections.Generic;
using ABI.CCK.Components;
using CVR.CCKEditor.ContentBuilder;
using UnityEngine;

namespace LensError
{
    /// <summary>
    /// Editor-only helper that tracks one or more YouTube playlists and syncs them
    /// into a CVRVideoPlayer on demand. Implements ICCKEditorOnly so CCK destroys it
    /// before world upload — no runtime overhead.
    /// </summary>
    [AddComponentMenu("LensError/YouTube Playlist Sync")]
    [DisallowMultipleComponent]
    public class YouTubePlaylistSync : MonoBehaviour, ICCKEditorOnly
    {
        [Serializable]
        public class SyncEntry
        {
            public string playlistUrl = "";
            public bool shuffle;
            public bool includeAgeRestricted;
            // Written by the custom editor after each successful sync
            [HideInInspector] public string lastSyncedUtc = "";
            [HideInInspector] public int    lastSyncedCount;
        }

        // If null the editor resolves the CVRVideoPlayer from this same GameObject
        public CVRVideoPlayer targetOverride;

        public List<SyncEntry> playlists = new List<SyncEntry>();
    }
}
