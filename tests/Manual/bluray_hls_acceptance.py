#!/usr/bin/env python3
"""Black-box Blu-ray HLS acceptance test using Jellyfin Desktop's mpv build.

This is intentionally a real-media/manual acceptance test. It exercises the
live Jellyfin API, HLS controller, FFmpeg process, and the exact mpv/FFmpeg
build bundled by jellyfin-desktop.
"""

from __future__ import annotations

import argparse
import dataclasses
import datetime as dt
import json
import os
import pathlib
import re
import shutil
import subprocess
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid
from typing import Any


DEFAULT_ITEM_ID = "74db55dd86c81b1de445d1d2ffee4364"
DEFAULT_PLAYLISTS = ("00100.MPLS", "00150.MPLS")
CURRENT_PLAYLIST = "CURRENT"
DEFAULT_SEEKS: dict[str, tuple[float, ...]] = {
    "00100.MPLS": (60.0, 900.0, 2673.62, 3384.5),
    "00150.MPLS": (60.0, 900.0, 2673.62, 3384.5),
}
TOKEN_PATTERN = re.compile(r"(?i)((?:api[_-]?key|token)=)[^&\s\"']+")
SEGMENT_PATTERN = re.compile(r"/hls\d+/[^/]+/(\d+)\.(?:mp4|ts)(?:\?|['\s])")


class AcceptanceError(RuntimeError):
    pass


def field(value: dict[str, Any], name: str, default: Any = None) -> Any:
    if name in value:
        return value[name]
    camel = name[:1].lower() + name[1:]
    return value.get(camel, default)


def sanitize(text: str) -> str:
    return TOKEN_PATTERN.sub(r"\1<redacted>", text)


def extract_device_profile(log_path: pathlib.Path) -> dict[str, Any]:
    prefix = "Device profile: "
    decoder = json.JSONDecoder()
    with log_path.open("r", encoding="utf-8", errors="replace") as source:
        for line in source:
            offset = line.find(prefix)
            if offset < 0:
                continue
            candidate = line[offset + len(prefix) :].lstrip()
            try:
                profile, _ = decoder.raw_decode(candidate)
            except json.JSONDecodeError:
                continue
            if isinstance(profile, dict):
                return profile
    raise AcceptanceError(f"No Jellyfin Desktop device profile found in {log_path}")


def load_device_profile(args: argparse.Namespace) -> dict[str, Any]:
    if args.device_profile_json:
        with args.device_profile_json.open("r", encoding="utf-8") as source:
            value = json.load(source)
        if not isinstance(value, dict):
            raise AcceptanceError("Device profile JSON must contain an object")
        return value

    candidates = []
    if args.device_profile_log:
        candidates.append(args.device_profile_log)
    candidates.extend(
        pathlib.Path.home() / name
        for name in ("stdout-good.txt", "stdout-bad2.txt", "stdout-bad.txt")
    )
    for candidate in candidates:
        if candidate.is_file():
            try:
                return extract_device_profile(candidate)
            except AcceptanceError:
                pass
    raise AcceptanceError(
        "A Jellyfin Desktop device profile is required. Pass --device-profile-log "
        "or --device-profile-json."
    )


class JellyfinClient:
    def __init__(self, server: str, username: str, password: str, timeout: float) -> None:
        self.server = server.rstrip("/")
        self.username = username
        self.password = password
        self.timeout = timeout
        self.device_id = "bluray-acceptance-" + uuid.uuid4().hex
        self.token: str | None = None
        self.user_id: str | None = None

    def _authorization(self, include_token: bool) -> str:
        parts = [
            'Client="Blu-ray HLS Acceptance"',
            'Device="Headless Jellyfin Desktop mpv"',
            f'DeviceId="{self.device_id}"',
            'Version="1"',
        ]
        if include_token and self.token:
            parts.append(f'Token="{self.token}"')
        return "MediaBrowser " + ", ".join(parts)

    def request(
        self,
        method: str,
        path: str,
        body: dict[str, Any] | None = None,
        *,
        timeout: float | None = None,
        expect_json: bool = True,
        authenticated: bool = True,
    ) -> Any:
        data = None
        headers = {"Accept": "application/json"}
        if body is not None:
            data = json.dumps(body, separators=(",", ":")).encode("utf-8")
            headers["Content-Type"] = "application/json"
        headers["Authorization"] = self._authorization(authenticated)
        request = urllib.request.Request(
            urllib.parse.urljoin(self.server + "/", path.lstrip("/")),
            data=data,
            headers=headers,
            method=method,
        )
        try:
            with urllib.request.urlopen(request, timeout=timeout or self.timeout) as response:
                payload = response.read()
        except urllib.error.HTTPError as error:
            details = error.read().decode("utf-8", errors="replace")
            raise AcceptanceError(
                f"{method} {path} returned HTTP {error.code}: {sanitize(details)[:500]}"
            ) from error
        except urllib.error.URLError as error:
            raise AcceptanceError(f"{method} {path} failed: {error.reason}") from error
        if not expect_json or not payload:
            return None
        try:
            return json.loads(payload)
        except json.JSONDecodeError as error:
            raise AcceptanceError(f"{method} {path} returned invalid JSON") from error

    def login(self) -> None:
        response = self.request(
            "POST",
            "/Users/AuthenticateByName",
            {"Username": self.username, "Pw": self.password},
            authenticated=False,
        )
        self.token = field(response, "AccessToken")
        user = field(response, "User", {})
        self.user_id = field(user, "Id")
        if not self.token or not self.user_id:
            raise AcceptanceError("Authentication response did not include a token and user id")

    def logout(self) -> None:
        if not self.token:
            return
        try:
            self.request("POST", "/Sessions/Logout", expect_json=False, timeout=10)
        except AcceptanceError:
            pass
        self.token = None

    def set_playlist(self, item_id: str, playlist: str) -> dict[str, Any]:
        response = self.request(
            "POST",
            f"/Videos/{item_id}/BluRay/Playlist",
            {"PlaylistName": playlist},
            timeout=max(self.timeout, 600),
        )
        effective = field(response, "EffectivePlaylistName")
        selected = field(response, "SelectedPlaylistName")
        valid = field(response, "SelectedPlaylistIsValid", False)
        refresh_error = field(response, "RefreshError")
        if selected != playlist or effective != playlist or not valid or refresh_error:
            raise AcceptanceError(
                f"Playlist selection failed: selected={selected!r}, effective={effective!r}, "
                f"valid={valid!r}, refresh_error={refresh_error!r}"
            )
        return response

    def clear_playlist(self, item_id: str) -> None:
        self.request(
            "DELETE",
            f"/Videos/{item_id}/BluRay/Playlist",
            timeout=max(self.timeout, 600),
        )

    def playback_url(
        self,
        item_id: str,
        profile: dict[str, Any],
        audio_stream_index: int | None,
    ) -> tuple[str, str, dict[str, Any]]:
        if not self.user_id or not self.token:
            raise AcceptanceError("Client is not authenticated")
        playback_request = {
            "UserId": self.user_id,
            "StartTimeTicks": 0,
            "IsPlayback": True,
            "AutoOpenLiveStream": True,
            "SubtitleStreamIndex": -1,
            "EnableDirectPlay": True,
            "EnableDirectStream": True,
            "AllowVideoStreamCopy": True,
            "AllowAudioStreamCopy": True,
            "MaxStreamingBitrate": 2_147_483_647,
            "AlwaysBurnInSubtitleWhenTranscoding": False,
            "DeviceProfile": profile,
        }
        if audio_stream_index is not None:
            playback_request["AudioStreamIndex"] = audio_stream_index

        response = self.request(
            "POST",
            f"/Items/{item_id}/PlaybackInfo",
            playback_request,
            timeout=max(self.timeout, 180),
        )
        sources = field(response, "MediaSources", [])
        if not sources:
            raise AcceptanceError("PlaybackInfo returned no media sources")
        source = sources[0]
        relative_url = field(source, "TranscodingUrl")
        if not relative_url:
            raise AcceptanceError(
                "PlaybackInfo did not return an HLS TranscodingUrl "
                f"(SupportsTranscoding={field(source, 'SupportsTranscoding')!r})"
            )
        url = urllib.parse.urljoin(self.server + "/", relative_url.lstrip("/"))
        parsed = urllib.parse.urlsplit(url)
        query = urllib.parse.parse_qsl(parsed.query, keep_blank_values=True)
        if audio_stream_index is not None:
            query = [
                (name, str(audio_stream_index) if name.lower() == "audiostreamindex" else value)
                for name, value in query
            ]
        names = {name.lower() for name, _ in query}
        if "apikey" not in names and "api_key" not in names:
            query.append(("ApiKey", self.token))
        url = urllib.parse.urlunsplit(parsed._replace(query=urllib.parse.urlencode(query)))
        query_values = dict(urllib.parse.parse_qsl(urllib.parse.urlsplit(url).query))
        play_session_id = query_values.get("PlaySessionId") or field(response, "PlaySessionId")
        if not play_session_id:
            raise AcceptanceError("Playback URL did not include a PlaySessionId")
        return url, play_session_id, source

    def stop_encoding(self, play_session_id: str) -> None:
        query = urllib.parse.urlencode(
            {"DeviceId": self.device_id, "PlaySessionId": play_session_id}
        )
        try:
            self.request(
                "DELETE",
                f"/Videos/ActiveEncodings?{query}",
                expect_json=False,
                timeout=15,
            )
        except AcceptanceError as error:
            print(f"warning: failed to stop encoding: {error}", file=sys.stderr)


@dataclasses.dataclass
class ScenarioResult:
    playlist: str
    passed: bool
    duration: float
    detail: str
    mpv_log: pathlib.Path | None = None
    video_codec: str | None = None
    video_copy: bool | None = None


def parse_seek_targets(value: str | None, playlist: str) -> tuple[float, ...]:
    if not value:
        return DEFAULT_SEEKS.get(playlist, DEFAULT_SEEKS["00100.MPLS"])
    targets = tuple(float(part) for part in value.split(",") if part.strip())
    if not targets:
        raise AcceptanceError("At least one seek target is required")
    return targets


def active_seek_segment_diagnostic(text: str) -> str | None:
    active_target: str | None = None
    segment_ids: set[int] = set()
    for line in text.splitlines():
        begin = re.search(r"BLURAY_ACCEPT_SEEK_BEGIN .*target=([0-9.]+)", line)
        if begin:
            active_target = begin.group(1)
            segment_ids = set()
            continue
        if active_target:
            match = SEGMENT_PATTERN.search(line)
            if match:
                segment_ids.add(int(match.group(1)))
        if active_target and "BLURAY_ACCEPT_SEEK_RECOVERED" in line:
            active_target = None
            segment_ids = set()

    if active_target and segment_ids:
        first = min(segment_ids)
        last = max(segment_ids)
        return (
            f"seek {active_target}s fetched {len(segment_ids)} distinct media segments "
            f"({first}..{last}) without restarting"
        )
    return None


def inspect_mpv_log(text: str, max_segments_per_seek: int) -> None:
    if "BLURAY_ACCEPT_PASS" not in text:
        failure = next(
            (line.strip() for line in text.splitlines() if "BLURAY_ACCEPT_FAIL" in line),
            "mpv exited without a pass marker",
        )
        segment_diagnostic = active_seek_segment_diagnostic(text)
        if segment_diagnostic:
            failure += f"; {segment_diagnostic}"
        raise AcceptanceError(failure)

    active_target: str | None = None
    segment_ids: set[int] = set()
    for line in text.splitlines():
        begin = re.search(r"BLURAY_ACCEPT_SEEK_BEGIN .*target=([0-9.]+)", line)
        if begin:
            active_target = begin.group(1)
            segment_ids = set()
            continue
        if active_target:
            match = SEGMENT_PATTERN.search(line)
            if match:
                segment_ids.add(int(match.group(1)))
        if active_target and "BLURAY_ACCEPT_SEEK_RECOVERED" in line:
            if len(segment_ids) > max_segments_per_seek:
                raise AcceptanceError(
                    f"seek {active_target}s fetched {len(segment_ids)} distinct media segments "
                    f"before playback recovered (maximum {max_segments_per_seek})"
                )
            active_target = None


def run_mpv(
    args: argparse.Namespace,
    url: str,
    playlist: str,
    targets: tuple[float, ...],
    artifact_dir: pathlib.Path,
) -> pathlib.Path:
    lua_script = pathlib.Path(__file__).with_suffix(".lua")
    raw_log = artifact_dir / f"mpv-{playlist.lower()}.raw.log"
    clean_log = artifact_dir / f"mpv-{playlist.lower()}.log"
    script_options = ",".join(
        (
            "bluray_acceptance-seeks=" + "|".join(f"{value:.3f}" for value in targets),
            f"bluray_acceptance-startup_timeout={args.startup_timeout}",
            f"bluray_acceptance-seek_timeout={args.seek_timeout}",
            f"bluray_acceptance-settle_seconds={args.settle_seconds}",
            f"bluray_acceptance-position_tolerance={args.position_tolerance}",
            f"bluray_acceptance-avsync_tolerance={args.avsync_tolerance}",
        )
    )
    command = [
        str(args.mpv),
        "--no-config",
        "--no-input-terminal",
        "--terminal=yes",
        "--vo=null",
        "--ao=null",
        "--keep-open=no",
        "--cache=yes",
        f"--network-timeout={max(args.seek_timeout, 20)}",
        "--msg-level=all=warn,cplayer=info,ffmpeg=debug,demuxer=debug,bluray_hls_acceptance=info",
        f"--script={lua_script}",
        f"--script-opts={script_options}",
        url,
    ]
    timeout = args.startup_timeout + (len(targets) * (args.seek_timeout + 2)) + 15
    started = time.monotonic()
    try:
        process = subprocess.run(
            command,
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            text=True,
            errors="replace",
            timeout=timeout,
            check=False,
        )
        output = process.stdout
    except subprocess.TimeoutExpired as error:
        output = (error.stdout or "") + "\nBLURAY_ACCEPT_FAIL reason=outer_timeout\n"
        if isinstance(output, bytes):
            output = output.decode("utf-8", errors="replace")
        process = subprocess.CompletedProcess(command, 124, output)
    raw_log.write_text(output, encoding="utf-8")
    clean_log.write_text(sanitize(output), encoding="utf-8")
    raw_log.unlink(missing_ok=True)
    if process.returncode != 0:
        failure = next(
            (line.strip() for line in output.splitlines() if "BLURAY_ACCEPT_FAIL" in line),
            f"mpv exited with status {process.returncode}",
        )
        segment_diagnostic = active_seek_segment_diagnostic(output)
        if segment_diagnostic:
            failure += f"; {segment_diagnostic}"
        raise AcceptanceError(
            f"{failure}; elapsed={time.monotonic() - started:.1f}s; log={clean_log}"
        )
    inspect_mpv_log(output, args.max_segments_per_seek)
    return clean_log


def collect_transcode_logs(
    log_dir: pathlib.Path | None,
    item_id: str,
    started_at: float,
    artifact_dir: pathlib.Path,
) -> list[pathlib.Path]:
    if not log_dir or not log_dir.is_dir():
        return []
    copied: list[pathlib.Path] = []
    normalized_id = item_id.replace("-", "")
    sources = list(log_dir.glob("FFmpeg.Transcode-*.log"))
    sources.extend(log_dir.glob("FFmpeg.DirectStream-*.log"))
    sources.extend(log_dir.glob("FFmpeg.Remux-*.log"))
    for source in sources:
        if source.stat().st_mtime < started_at - 2:
            continue
        if normalized_id not in source.name.replace("-", ""):
            continue
        destination = artifact_dir / source.name
        destination.write_text(sanitize(source.read_text(errors="replace")), encoding="utf-8")
        copied.append(destination)
    return copied


def inspect_transcode_logs(paths: list[pathlib.Path], require_video_copy: bool = False) -> bool | None:
    failures = (
        "Cannot write moov atom before",
        "audio:0KiB",
        "Error initializing output stream",
        "Conversion failed",
    )
    video_copy: bool | None = None
    for path in paths:
        text = path.read_text(encoding="utf-8", errors="replace")
        for failure in failures:
            if failure in text:
                raise AcceptanceError(f"server transcode log contains {failure!r}: {path}")
        command = next((line for line in text.splitlines() if line.startswith("ffmpeg ")), None)
        if command is not None:
            command_copies_video = "-codec:v:0 copy" in command or "-c:v:0 copy" in command
            video_copy = command_copies_video if video_copy is None else video_copy and command_copies_video
            if require_video_copy and not command_copies_video:
                raise AcceptanceError(f"server encoded video instead of copying it: {path}")

    if require_video_copy and not paths:
        raise AcceptanceError("no server FFmpeg job logs were collected to verify video copy")

    return video_copy


def default_mpv(repo_root: pathlib.Path) -> pathlib.Path:
    return repo_root.parent / "jellyfin-desktop" / "build" / "mpv-build" / "mpv"


def parse_args() -> argparse.Namespace:
    repo_root = pathlib.Path(__file__).resolve().parents[2]
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--server", default=os.environ.get("JELLYFIN_URL", "http://10.42.34.131:8096"))
    parser.add_argument("--username", default=os.environ.get("JELLYFIN_USERNAME", "ian"))
    parser.add_argument("--password", default=os.environ.get("JELLYFIN_PASSWORD"))
    parser.add_argument("--item-id", default=os.environ.get("JELLYFIN_BLURAY_ITEM_ID", DEFAULT_ITEM_ID))
    parser.add_argument("--playlists", nargs="+", default=list(DEFAULT_PLAYLISTS))
    parser.add_argument(
        "--audio-stream-index",
        type=int,
        help="Force an audio stream index; by default Jellyfin selects the first audio stream",
    )
    parser.add_argument(
        "--require-video-copy",
        action="store_true",
        help="Fail if any collected FFmpeg command encodes rather than copies video",
    )
    parser.add_argument("--mode", choices=("quick", "full"), default="quick")
    parser.add_argument("--seeks", help="Comma-separated override applied to every playlist")
    parser.add_argument("--mpv", type=pathlib.Path, default=default_mpv(repo_root))
    parser.add_argument("--device-profile-log", type=pathlib.Path)
    parser.add_argument("--device-profile-json", type=pathlib.Path)
    parser.add_argument("--startup-timeout", type=float, default=60)
    parser.add_argument("--seek-timeout", type=float, default=30)
    parser.add_argument("--settle-seconds", type=float, default=2)
    parser.add_argument("--playlist-timeout", type=float, default=600)
    parser.add_argument("--position-tolerance", type=float, default=1.5)
    parser.add_argument("--avsync-tolerance", type=float, default=1.0)
    # A copied Blu-ray GOP can span several six-second HLS fragments, and mpv
    # prefetches beyond the recovery point. Keep this bound low enough to catch
    # the historical linear timeline walk while allowing bounded decoder
    # recovery on irregularly authored streams.
    parser.add_argument("--max-segments-per-seek", type=int, default=12)
    parser.add_argument(
        "--server-log-dir",
        type=pathlib.Path,
        default=pathlib.Path.home() / ".local/share/jellyfin/log",
    )
    parser.add_argument(
        "--artifacts",
        type=pathlib.Path,
        default=pathlib.Path("/tmp/jellyfin-bluray-acceptance"),
    )
    parser.add_argument("--stop-on-failure", action="store_true")
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    if not args.password:
        print("error: set JELLYFIN_PASSWORD or pass --password", file=sys.stderr)
        return 2
    if not args.mpv.is_file():
        print(f"error: mpv executable not found: {args.mpv}", file=sys.stderr)
        return 2

    try:
        profile = load_device_profile(args)
    except (AcceptanceError, OSError, json.JSONDecodeError) as error:
        print(f"error: {error}", file=sys.stderr)
        return 2

    timestamp = dt.datetime.now().strftime("%Y%m%d-%H%M%S")
    artifact_dir = args.artifacts / timestamp
    artifact_dir.mkdir(parents=True, exist_ok=False)
    (artifact_dir / "device-profile.json").write_text(
        json.dumps(profile, indent=2, sort_keys=True), encoding="utf-8"
    )

    client = JellyfinClient(args.server, args.username, args.password, args.playlist_timeout)
    results: list[ScenarioResult] = []
    print(f"Artifacts: {artifact_dir}")
    print(f"Server: {args.server}")
    print(f"mpv: {args.mpv}")

    try:
        client.login()
        if args.mode == "full":
            print("Preparing cold selection state (automatic playlist)...", flush=True)
            client.clear_playlist(args.item_id)

        for playlist in args.playlists:
            scenario_started = time.monotonic()
            wall_started = time.time()
            play_session_id: str | None = None
            use_current_playlist = playlist.upper() == CURRENT_PLAYLIST
            try:
                runtime_ticks = None
                if use_current_playlist:
                    print(f"[{playlist}] using current playlist selection...", flush=True)
                else:
                    print(f"[{playlist}] selecting playlist...", flush=True)
                    selection = client.set_playlist(args.item_id, playlist)
                    runtime_ticks = next(
                        (
                            field(candidate, "RunTimeTicks")
                            for candidate in field(selection, "Playlists", [])
                            if field(candidate, "Name") == playlist
                        ),
                        None,
                    )
                    print(
                        f"[{playlist}] selected; runtime="
                        f"{runtime_ticks / 10_000_000:.3f}s" if runtime_ticks else f"[{playlist}] selected",
                        flush=True,
                    )
                url, play_session_id, source = client.playback_url(
                    args.item_id, profile, args.audio_stream_index
                )
                video_stream = next(
                    (
                        stream
                        for stream in field(source, "MediaStreams", [])
                        if field(stream, "Type") == "Video"
                    ),
                    None,
                )
                video_codec = field(video_stream, "Codec") if video_stream else None
                source_playlist = field(source, "BluRayPlaylistName")
                runtime_ticks = runtime_ticks or field(source, "RunTimeTicks")
                if use_current_playlist:
                    print(
                        f"[{playlist}] PlaybackInfo selected {source_playlist or 'an unspecified playlist'}",
                        flush=True,
                    )
                elif source_playlist and source_playlist.upper() != playlist.upper():
                    raise AcceptanceError(
                        f"PlaybackInfo used {source_playlist}, expected {playlist}"
                    )
                targets = parse_seek_targets(args.seeks, playlist)
                if runtime_ticks:
                    runtime_seconds = runtime_ticks / 10_000_000
                    targets = tuple(value for value in targets if value < runtime_seconds - 5)
                print(
                    f"[{playlist}] starting mpv; seeks="
                    + ",".join(f"{value:g}" for value in targets),
                    flush=True,
                )
                mpv_log = run_mpv(args, url, playlist, targets, artifact_dir)
                client.stop_encoding(play_session_id)
                play_session_id = None
                time.sleep(1)
                server_logs = collect_transcode_logs(
                    args.server_log_dir, args.item_id, wall_started, artifact_dir
                )
                video_copy = inspect_transcode_logs(server_logs, args.require_video_copy)
                duration = time.monotonic() - scenario_started
                results.append(
                    ScenarioResult(
                        playlist,
                        True,
                        duration,
                        "ok",
                        mpv_log,
                        video_codec,
                        video_copy,
                    )
                )
                print(f"[{playlist}] PASS in {duration:.1f}s", flush=True)
            except (AcceptanceError, OSError, subprocess.SubprocessError) as error:
                if play_session_id:
                    client.stop_encoding(play_session_id)
                time.sleep(1)
                server_logs = collect_transcode_logs(
                    args.server_log_dir, args.item_id, wall_started, artifact_dir
                )
                duration = time.monotonic() - scenario_started
                detail = sanitize(str(error))
                try:
                    inspect_transcode_logs(server_logs, args.require_video_copy)
                except AcceptanceError as server_error:
                    detail += f"; {sanitize(str(server_error))}"
                failed_mpv_log = artifact_dir / f"mpv-{playlist.lower()}.log"
                results.append(ScenarioResult(playlist, False, duration, detail))
                if failed_mpv_log.is_file():
                    results[-1].mpv_log = failed_mpv_log
                print(f"[{playlist}] FAIL in {duration:.1f}s: {detail}", file=sys.stderr, flush=True)
                if args.stop_on_failure:
                    break
    except (AcceptanceError, OSError, json.JSONDecodeError) as error:
        print(f"fatal: {sanitize(str(error))}", file=sys.stderr)
        return 2
    finally:
        client.logout()

    summary = {
        "mode": args.mode,
        "server": args.server,
        "item_id": args.item_id,
        "results": [dataclasses.asdict(result) | {"mpv_log": str(result.mpv_log) if result.mpv_log else None} for result in results],
    }
    (artifact_dir / "summary.json").write_text(json.dumps(summary, indent=2), encoding="utf-8")
    print("\nSummary:")
    for result in results:
        state = "PASS" if result.passed else "FAIL"
        print(f"  {result.playlist}: {state} ({result.duration:.1f}s) {result.detail}")
    return 0 if results and all(result.passed for result in results) else 1


if __name__ == "__main__":
    raise SystemExit(main())
