# Video On Demand for Emby

Browse, search, and play movies and TV episodes from an M3U playlist in a dedicated Emby webview. Supports season selection, paginated results, and daily TMDB popularity rankings.

## Install

1. Download `EmbyVideoOnDemand.dll` from [Releases](../../releases/latest).
2. Copy it into your Emby Server program data `plugins` directory and restart Emby.
3. In **Dashboard → Video On Demand**, enter your M3U URL and save. You can also add an XMLTV URL under **VoD EPG URL** and optional TMDB, TheTVDB, or OMDb credentials. Matching EPG fields take priority.

Sign in to Emby Web, then open `/emby/Vod/Browse` on your server. If your Emby installation uses a base path, keep it before `/emby`.

## Open the webview from the Emby sidebar

1. Back up `system/dashboard-ui/index.html` in the Emby Server installation directory. This is the web app entry file, separate from program data.
2. Add this line immediately before `</body>`. If an older `vod-sidebar-integration` tag exists, replace it.

   ```html
   <script defer id="vod-sidebar-integration" src="../Vod/Assets/sidebar.js?v=1.4.8"></script>
   ```

3. Hard-refresh Emby Web, then click **Video On Demand** in the sidebar. The hook applies to the server-hosted Emby Web app. Emby updates may replace the entry file, so reapply the line if needed.

## Build

Run `dotnet build -c Release` with the .NET 8 SDK. Tested on Emby Server 4.10.2.0.

This product uses the TMDB API but is not endorsed or certified by TMDB.
