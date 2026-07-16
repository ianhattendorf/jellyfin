local mp = require "mp"
local options = require "mp.options"

local opts = {
    seeks = "",
    startup_timeout = 60,
    seek_timeout = 30,
    settle_seconds = 0.75,
    position_tolerance = 1.5,
    avsync_tolerance = 1.0,
}

options.read_options(opts, "bluray_acceptance")

local targets = {}
for value in string.gmatch(opts.seeks, "[^|]+") do
    local target = tonumber(value)
    if target then
        table.insert(targets, target)
    end
end

local phase = "startup"
local target_index = 0
local current_target = nil
local phase_started = mp.get_time()
local deadline = nil
local finished = false

local function marker(name, fields)
    local parts = { "BLURAY_ACCEPT_" .. name }
    if fields then
        for key, value in pairs(fields) do
            table.insert(parts, string.format("%s=%s", key, tostring(value)))
        end
    end

    mp.msg.info(table.concat(parts, " "))
end

local function cancel_deadline()
    if deadline then
        deadline:kill()
        deadline = nil
    end
end

local function fail(reason)
    if finished then
        return
    end

    finished = true
    cancel_deadline()
    marker("FAIL", {
        phase = phase,
        reason = string.gsub(reason, "%s+", "_"),
        target = current_target or "none",
    })
    mp.commandv("quit", "2")
end

local function arm_deadline(seconds, reason)
    cancel_deadline()
    deadline = mp.add_timeout(seconds, function()
        fail(reason)
    end)
end

local function selected_track(track_type)
    local tracks = mp.get_property_native("track-list", {})
    for _, track in ipairs(tracks) do
        if track.type == track_type and track.selected then
            return track
        end
    end

    return nil
end

local function validate_av()
    if not selected_track("video") then
        return false, "no_selected_video_track"
    end

    if not selected_track("audio") then
        return false, "no_selected_audio_track"
    end

    if not mp.get_property_native("video-params") then
        return false, "video_decoder_not_ready"
    end

    if not mp.get_property_native("audio-params") then
        return false, "audio_decoder_not_ready"
    end

    return true, nil
end

local function begin_next_seek()
    target_index = target_index + 1
    current_target = targets[target_index]
    if not current_target then
        finished = true
        cancel_deadline()
        marker("PASS", {
            seeks = #targets,
            final_position = string.format("%.3f", mp.get_property_number("time-pos", -1)),
        })
        mp.commandv("quit", "0")
        return
    end

    phase = "seek"
    phase_started = mp.get_time()
    marker("SEEK_BEGIN", {
        index = target_index,
        target = string.format("%.3f", current_target),
    })
    arm_deadline(opts.seek_timeout, "seek_timeout")
    mp.commandv("seek", tostring(current_target), "absolute+exact")
end

local function verify_position_advances(start_position, callback)
    mp.add_timeout(opts.settle_seconds, function()
        if finished then
            return
        end

        local position = mp.get_property_number("time-pos", -1)
        if position <= start_position + 0.1 then
            fail("position_not_advancing")
            return
        end

        callback(position)
    end)
end

local function on_playback_restart()
    if finished then
        return
    end

    local av_ok, av_error = validate_av()
    if not av_ok then
        -- A playback-restart event can precede final audio initialization by a
        -- few milliseconds. Poll briefly, while retaining the phase deadline.
        mp.add_timeout(0.1, on_playback_restart)
        return
    end

    local position = mp.get_property_number("time-pos", -1)
    if position < 0 then
        fail(av_error or "missing_time_position")
        return
    end

    if phase == "startup" then
        cancel_deadline()
        verify_position_advances(position, function(advanced_position)
            marker("START_OK", {
                elapsed = string.format("%.3f", mp.get_time() - phase_started),
                position = string.format("%.3f", advanced_position),
            })
            begin_next_seek()
        end)
        return
    end

    if phase ~= "seek" then
        return
    end

    local delta = math.abs(position - current_target)
    if delta > opts.position_tolerance then
        fail(string.format("position_delta_%.3f", delta))
        return
    end

    local avsync = mp.get_property_number("avsync", 0)
    if math.abs(avsync) > opts.avsync_tolerance then
        fail(string.format("avsync_%.3f", avsync))
        return
    end

    cancel_deadline()
    marker("SEEK_RECOVERED", {
        actual = string.format("%.3f", position),
        elapsed = string.format("%.3f", mp.get_time() - phase_started),
        index = target_index,
        target = string.format("%.3f", current_target),
    })
    verify_position_advances(position, function(advanced_position)
        marker("SEEK_OK", {
            actual = string.format("%.3f", advanced_position),
            avsync = string.format("%.3f", avsync),
            elapsed = string.format("%.3f", mp.get_time() - phase_started),
            index = target_index,
            target = string.format("%.3f", current_target),
        })
        begin_next_seek()
    end)
end

mp.register_event("playback-restart", on_playback_restart)
mp.register_event("end-file", function(event)
    if not finished and event.reason ~= "quit" then
        fail("end_file_" .. tostring(event.reason))
    end
end)

marker("START", { seeks = #targets })
arm_deadline(opts.startup_timeout, "startup_timeout")
