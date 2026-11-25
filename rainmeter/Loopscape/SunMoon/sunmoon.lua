-- Sun & Moon widget: reads sunrise / sunset from WeatherFetch.exe's output (key=value lines: sunrise=HH:mm,
-- sunset=HH:mm, sunrise2=HH:mm for tomorrow) and places the sun on its arc; works out the moon's phase from the date.

local mRun
local shown = {}   -- values already sent, so a minute that changes nothing costs no bangs
local dirty = false

function Initialize()
  mRun = SKIN:GetMeasure('MeasureSunRun')
end

local function setVar(name, value)
  value = tostring(value)
  if shown['$' .. name] == value then return end
  shown['$' .. name] = value
  SKIN:Bang('!SetVariable', name, value)
  dirty = true
end
local function setText(meter, value)
  if shown[meter] == value then return end
  shown[meter] = value
  SKIN:Bang('!SetOption', meter, 'Text', value)
  dirty = true
end

local function parse(out)
  local t = {}
  for line in (out or ''):gmatch('[^\r\n]+') do
    local k, v = line:match('^(%w+)=(.*)$')
    if k then t[k] = v end
  end
  return t
end

local function minutes(hm)
  local h, m = (hm or ''):match('^(%d%d?):(%d%d)$')
  if not h then return nil end
  return tonumber(h) * 60 + tonumber(m)
end

-- 1050 -> "17:30" or "5:30 PM" (Settings > Clock format)
local function clock(mins)
  mins = math.floor(mins + 0.5) % 1440
  local h, m = math.floor(mins / 60), mins % 60
  if SKIN:GetVariable('ClockFormat', '24') == '12' then
    return string.format('%d:%02d %s', (h + 11) % 12 + 1, m, h < 12 and 'AM' or 'PM')
  end
  return string.format('%d:%02d', h, m)
end

-- 297 -> "4 h 57 min", 40 -> "40 min"
local function span(mins)
  mins = math.max(0, math.floor(mins + 0.5))
  local h, m = math.floor(mins / 60), mins % 60
  if h == 0 then return m .. ' min' end
  if m == 0 then return h .. ' h' end
  return h .. ' h ' .. m .. ' min'
end

-- ---------- moon ----------
local SYNODIC = 29.530588853        -- days from new moon to new moon
local NEW_MOON_JD = 2451550.1       -- a new moon (6 January 2000), as a Julian day

local PHASES = {
  { 1.85, 'New moon' }, { 5.54, 'Waxing crescent' }, { 9.23, 'First quarter' }, { 12.92, 'Waxing gibbous' },
  { 16.61, 'Full moon' }, { 20.30, 'Waning gibbous' }, { 23.99, 'Last quarter' }, { 27.68, 'Waning crescent' },
  { 99, 'New moon' },
}

local function moon()
  local jd = os.time() / 86400 + 2440587.5
  local age = (jd - NEW_MOON_JD) % SYNODIC
  local k = math.cos(2 * math.pi * age / SYNODIC)   -- 1 new, -1 full
  local lit = (1 - k) / 2
  local name
  for _, p in ipairs(PHASES) do if age < p[1] then name = p[2]; break end end
  -- the icon: the lit half (right while waxing, left while waning), then the terminator's ellipse taken away
  -- (crescent) or added (gibbous)
  local waxing = age < SYNODIC / 2
  setVar('MoonDarkX', waxing and 0 or 8)
  setVar('MoonRX', string.format('%.2f', math.max(0.3, 7 * math.abs(k))))
  setVar('MoonOp', k > 0 and 'Exclude' or 'Union')
  -- days until the next full / new moon
  local nextName, days
  if waxing then nextName, days = 'full', SYNODIC / 2 - age else nextName, days = 'new', SYNODIC - age end
  local d = math.floor(days + 0.5)
  local when = d <= 0 and ('today') or (d == 1 and 'tomorrow' or ('in ' .. d .. ' days'))
  local text = name .. '  [\\x00B7]  ' .. math.floor(lit * 100 + 0.5) .. '% lit'
  if name ~= 'Full moon' and name ~= 'New moon' then text = text .. '  [\\x00B7]  ' .. nextName .. ' ' .. when end
  setText('MeterMoon', text)
end

-- ---------- sun ----------
local function placeSun(t, day)
  local w = tonumber(SKIN:GetVariable('ArcW', '220')) or 220
  local h = tonumber(SKIN:GetVariable('ArcH', '58')) or 58
  local rx, ry, base = w / 2 - 8, h - 8, h - 1
  t = math.max(0.004, math.min(0.996, t))
  setVar('ArcRX', rx); setVar('ArcRY', ry); setVar('ArcBase', base)
  setVar('SunX', string.format('%.1f', w / 2 - rx * math.cos(math.pi * t)))
  setVar('SunY', string.format('%.1f', base - ry * math.sin(math.pi * t)))
  setVar('SunFill', day and 6.5 or 0.01)   -- a filled sun by day, a ring at night
end

function Update()
  local out = mRun and mRun:GetStringValue() or ''
  local w = parse(out)
  local rise, set = minutes(w.sunrise), minutes(w.sunset)
  local rise2 = minutes(w.sunrise2) or rise
  local now = os.date('*t')
  local n = now.hour * 60 + now.min + now.sec / 60

  if w.error and not rise then
    setText('MeterName', 'Sun & Moon')
    setText('MeterInfo', w.error)
    placeSun(0.5, true)
  elseif not rise or not set then
    setText('MeterName', out == '' and '...' or 'No sun times today')
    setText('MeterInfo', '')
    placeSun(0.5, true)
  elseif n >= rise and n < set then
    placeSun((n - rise) / (set - rise), true)
    setText('MeterName', 'Sunset ' .. clock(set))
    setText('MeterInfo', 'in ' .. span(set - n) .. '  [\\x00B7]  ' .. span(set - rise) .. ' daylight')
  else
    -- night: from sunset to the next sunrise
    local from, to
    if n >= set then from, to = set, rise2 + 1440 else from, to = set - 1440, rise end
    placeSun((n - from) / (to - from), false)
    setText('MeterName', 'Sunrise ' .. clock(to))
    setText('MeterInfo', 'in ' .. span(to - n) .. '  [\\x00B7]  sunset was ' .. clock(set))
  end
  moon()

  if dirty then
    dirty = false
    -- the meters only update when asked (DefaultUpdateDivider=-1): two passes, because the panel and the pills come
    -- before the text they wrap
    SKIN:Bang('!UpdateMeter', '*')
    SKIN:Bang('!UpdateMeter', '*')
    SKIN:Bang('!Redraw')
  end
  return 0
end
