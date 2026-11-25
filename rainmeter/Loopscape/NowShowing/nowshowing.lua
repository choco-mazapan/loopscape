-- Now Showing widget: shows what Loopscape is playing (@Resources\wallstatus.txt) and sends the buttons' commands
-- (@Resources\wallcmd.txt, one per line: next, prev, pause, play, pick <style|rotate>).
--
-- wallstatus.txt (written by Loopscape when something changes):
--   style=<file name>  name=<display name>  reason=Day rotation|Night rotation|Rotation|Weather: rain|One style|...
--   next=HH:mm[ (sunset)]  nextat=<Unix time of the next change, 0 = none>  rotating=0|1  paused=0|1
--   state=<paused / on battery / downloading ..., or empty>

local RES, STYLES
local status = {}
local shown = {}      -- option/variable values already sent, so a tick that changes nothing costs no bangs
local dirty = false
local thumbFor = nil  -- style whose thumbnail is loaded

local DOT = '  [\\x00B7]  '
local GLYPH = { prev = '[\\xE892]', next = '[\\xE893]', play = '[\\xE768]', pause = '[\\xE769]', pin = '[\\xE718]', unpin = '[\\xE77A]' }

function Initialize()
  RES = SKIN:GetVariable('@')
  STYLES = (os.getenv('USERPROFILE') or '') .. '\\Loopscape\\styles\\'
end

local function readStatus()
  local f = io.open(RES .. 'wallstatus.txt', 'r')
  if not f then return {} end
  local t = {}
  for line in f:lines() do local k, v = line:match('^(%w+)=(.*)$'); if k then t[k] = v end end
  f:close()
  return t
end

local function exists(path)
  local f = io.open(path, 'rb')
  if f then f:close(); return true end
  return false
end

local function setOption(meter, option, value)
  local k = meter .. '|' .. option
  if shown[k] == value then return end
  shown[k] = value
  SKIN:Bang('!SetOption', meter, option, value)
  dirty = true
end
local function setVariable(name, value)
  value = tostring(value)
  if shown['$' .. name] == value then return end
  shown['$' .. name] = value
  SKIN:Bang('!SetVariable', name, value)
  dirty = true
end

local function cap(s) return (s:gsub('^%l', string.upper)) end

-- "Day rotation  ·  next in 23 min", "Paused  ·  next at 19:12 (sunset)", "Kept on screen  ·  rotation off", ...
local function infoText(s)
  local rotating = s.rotating == '1'
  local when = nil
  local at = tonumber(s.nextat or '') or 0
  if rotating and at > 0 then
    local left = at - os.time()
    if left < -120 then when = nil   -- Loopscape isn't updating the file (not running): don't count down forever
    elseif left < 60 then when = 'next in under a minute'
    elseif left < 3600 then when = 'next in ' .. math.floor(left / 60 + 0.5) .. ' min'
    elseif s.next and s.next ~= '' then when = 'next at ' .. s.next end
  end
  local state = s.state or ''
  -- paused under a window that covers the desktop: that window covers this widget too, so it's not news
  state = state:gsub(';?%s*paused %(window covers the desktop%)', '')
  if state == '' and s.paused == '1' then state = 'paused' end
  if state == 'paused' then return 'Paused' .. (when and (DOT .. when) or '') end
  if state ~= '' then return cap(state) end   -- on battery, a download in progress, ...
  if not rotating then return 'Kept on screen' .. DOT .. 'rotation off' end
  local reason = s.reason or ''
  if when then return (reason ~= '' and (reason .. DOT) or '') .. when end
  return reason
end

local function show()
  local s = status
  local style = s.style or ''
  setOption('MeterName', 'Text', (s.name and s.name ~= '') and s.name or (style ~= '' and cap(style) or 'Loopscape is not running'))
  setOption('MeterInfo', 'Text', style ~= '' and infoText(s) or 'Start it from the Start menu')
  setOption('MeterBtn2', 'Text', s.paused == '1' and GLYPH.play or GLYPH.pause)
  setOption('MeterBtn4', 'Text', s.rotating == '0' and GLYPH.unpin or GLYPH.pin)
  if style ~= thumbFor then
    thumbFor = style
    local png = STYLES .. style .. '.png'
    local ok = style ~= '' and style ~= 'loop' and exists(png)
    setOption('MeterThumb', 'ImageName', ok and png or '')
    setVariable('ThumbOn', ok and 1 or 0)
  end
  if dirty then
    dirty = false
    SKIN:Bang('!UpdateMeter', '*')
    SKIN:Bang('!Redraw')
  end
end

function Update()
  status = readStatus()
  show()
  return status.name or ''
end

-- ---------- buttons ----------
function Cmd(c)
  local f = io.open(RES .. 'wallcmd.txt', 'a')
  if not f then return end
  f:write(c, '\n')
  f:close()
  -- read the status every 150 ms for a few seconds, so the change shows as soon as Loopscape has made it
  SKIN:Bang('!CommandMeasure', 'MeasureFastTick', 'Execute 6')
end

function TogglePause()
  local pausing = status.paused ~= '1'
  Cmd(pausing and 'pause' or 'play')
  status.paused = pausing and '1' or '0'   -- show the new glyph now; the next status read confirms it
  show()
end

-- Keep this style (stop rotating) or go back to rotating
function TogglePin()
  local style = status.style or ''
  if status.rotating == '0' then Cmd('pick rotate')
  elseif style ~= '' then Cmd('pick ' .. (style == 'loop' and 'gradient' or style)) end
end

function Hover(n)
  setVariable('BtnHover', tonumber(n) or 0)
  if dirty then
    dirty = false
    for i = 1, 4 do SKIN:Bang('!UpdateMeter', 'Pill_MeterBtn' .. i) end
    SKIN:Bang('!Redraw')
  end
end
