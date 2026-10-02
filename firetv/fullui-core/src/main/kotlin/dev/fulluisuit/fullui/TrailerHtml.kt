package dev.fulluisuit.fullui

/**
 * HTML page for the WebView-based YouTube trailer player (YouTube IFrame Player API).
 *
 * The page reports back through a JavaScript interface named `FullUiBridge` with
 * `onPlaying()`, `onEnded()` and `onError(code)`. The host calls `setMuted(boolean)`.
 */
object TrailerHtml {
    private val VIDEO_ID = Regex("^[A-Za-z0-9_-]{6,20}$")

    /** Base URL to load the page with, so the embed sees a normal https origin. */
    const val BASE_URL = "https://www.youtube.com"

    /** A YouTube id is safe to put in JavaScript only when it is plain id characters. */
    fun isValidVideoId(key: String?): Boolean = key != null && VIDEO_ID.matches(key)

    /** @return the page, or null when [videoKey] is not a plausible YouTube id. */
    fun page(
        videoKey: String,
        muted: Boolean = true,
    ): String? {
        if (!isValidVideoId(videoKey)) return null
        return """<!DOCTYPE html>
<html><head><meta name="viewport" content="width=device-width, initial-scale=1">
<style>html,body{margin:0;padding:0;background:#000;overflow:hidden;width:100%;height:100%}
#p,iframe{position:absolute;top:0;left:0;width:100%;height:100%;border:0;pointer-events:none}</style></head>
<body><div id="p"></div>
<script>
var player = null;
var wantMuted = $muted;
function setMuted(m){ wantMuted = m; if(player && player.mute){ if(m){player.mute();}else{player.unMute();} } }
function onYouTubeIframeAPIReady(){
  player = new YT.Player('p', {
    width: '100%', height: '100%', videoId: '$videoKey',
    playerVars: {autoplay:1, mute:1, controls:0, rel:0, playsinline:1, modestbranding:1,
                 iv_load_policy:3, disablekb:1, fs:0, showinfo:0},
    events: {
      onReady: function(e){ e.target.mute(); e.target.playVideo(); },
      onStateChange: function(e){
        if(e.data === 1){ setMuted(wantMuted); FullUiBridge.onPlaying(); }
        else if(e.data === 0){ FullUiBridge.onEnded(); }
      },
      onError: function(e){ FullUiBridge.onError(e.data); }
    }
  });
}
</script>
<script src="https://www.youtube.com/iframe_api" onerror="FullUiBridge.onError(-1)"></script>
</body></html>"""
    }
}
