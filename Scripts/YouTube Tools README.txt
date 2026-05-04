YouTube Tools for CVR Video Player
====================================

Two editor tools for populating CVRVideoPlayer playlists from YouTube.
Neither tool affects runtime. All code is editor-only and stripped before world upload.


GETTING A YOUTUBE API KEY
--------------------------
1. Go to console.cloud.google.com
2. Create a project (or select an existing one)
3. APIs & Services > Enable APIs > search "YouTube Data API v3" > Enable
4. APIs & Services > Credentials > Create Credentials > API Key
5. Copy the key and paste it into either tool below (saved once, shared by both)


TOOL 1: YOUTUBE PLAYLIST IMPORTER  (LensError > YouTube Playlist Importer)
---------------------------------------------------------------------------
One-shot import with a preview before committing anything.

Usage:
  1. Paste your API key and click Save Key (only needed once).
  2. Paste a playlist URL, channel URL, or bare ID into "Playlist / Channel URL or ID".
     Accepted formats:
       https://www.youtube.com/playlist?list=PLxxx
       https://www.youtube.com/watch?v=xxx&list=PLxxx
       PLxxx  (bare playlist ID)
       https://www.youtube.com/@handle    (channel, imports all uploads)
       https://www.youtube.com/channel/UCxxx
       https://www.youtube.com/user/username
       @handle  (bare handle)
       UCxxx    (bare channel ID)
  3. Drag a CVRVideoPlayer from the scene into "Target Video Player",
     or pick one from the "Scene Players" dropdown and hit Scan.
  4. Set options:
       Shuffle on Import    randomise order before writing to the player
       Sort Order           Playlist Order / Newest First / Oldest First
                            (grayed out when Shuffle is on)
       Append to Existing   keep other playlists already on the player
                            (if off, all existing playlists are cleared first)
  5. Click "Fetch Playlist". Videos appear in a scrollable preview list.
  6. Review the list. Each video is colour-coded:
       Green   available, checked by default
       Yellow  age-restricted, unchecked by default (check manually to include)
       Red     deleted / private / unavailable, always excluded
     Use All / None buttons or individual checkboxes to adjust the selection.
  7. Click "Import N Videos into Player".

Same-playlist behaviour:
  If the player already has a playlist with the same title, the importer merges
  into it. Only videos not already present are added. Re-importing with Shuffle
  on reshuffles the whole playlist.

Chunking:
  Playlists over 200 videos are automatically split into
  "Title (1)", "Title (2)", etc. on the player. Re-syncing fills existing chunks
  before creating new ones.


TOOL 2: YOUTUBE PLAYLIST SYNC COMPONENT
----------------------------------------
Attach to any GameObject that also has a CVRVideoPlayer.
Tracks one or more playlists and re-syncs them on demand.
Stripped at world upload (ICCKEditorOnly), zero runtime overhead.

Setup:
  1. Add Component > LensError > YouTube Playlist Sync
  2. Enter and save your API key in the "YouTube API Key" foldout in the Inspector
     (same key as the Importer, set it in either place, both share it).
  3. If the CVRVideoPlayer is on a different GameObject, assign it to
     "Target Override". Otherwise it is found automatically on the same object.
  4. Click "+ Add Playlist" for each YouTube playlist or channel you want to manage.
     Per entry:
       URL or ID              same formats as the Importer (playlists and channels)
       Shuffle on Sync        randomise order when syncing
       Sort Order             Playlist Order / Newest First / Oldest First
                              (grayed out when Shuffle is on)
       Include Age-Restricted include age-restricted videos (off by default)
  5. Click "Sync Now" on an individual entry, or "Sync All Playlists" to
     update everything at once.
  6. Last-synced timestamp and video count are shown per entry after each sync.

Sync behaviour:
  Same merge and chunk logic as the Importer. Only new videos are added,
  nothing is removed. Playlists over 200 videos are chunked automatically.
  Re-syncing with Shuffle on reshuffles the existing playlist(s).

  Sync does NOT happen automatically on world upload. Press Sync before
  uploading when you want the latest videos.

Sort order:
  Playlist Order   keeps the original YouTube playlist order (default)
  Newest First     sorts by video publish date, newest at the top
  Oldest First     sorts by video publish date, oldest at the top
  If Shuffle is enabled the sort order setting is ignored.
  In the merge case, sort order only affects the sequence of newly added videos.

Video filtering (both tools):
  Deleted          always excluded
  Private          always excluded
  Unavailable      always excluded
  Age-restricted   excluded by default; opt-in per playlist (Sync component)
                   or per video (Importer preview list)
