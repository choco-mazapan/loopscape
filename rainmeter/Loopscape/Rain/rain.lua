-- Rain widget: WeatherFetch.exe's rain=<start>|v1,v2,... line is the precipitation (mm) in each 15 minutes from
-- <start> (local time, e.g. 2026-11-24T14:15). Lined up with the clock, the next 12 steps (3 hours) give the text
-- ("Rain in ~20 min", "Raining  ·  stops in ~40 min", "Dry for the next 3 h") and the bars.

local mRun
local shown = {}
local dirty = false

local WET = 0.05               -- mm in 15 minutes that counts as rain (Open-Meteo reports 0.1 mm steps)
local LIGHT, MODERATE = 0.6, 2 -- below 2.5 mm/h is light, below 8 mm/h moderate, then heavy
local STEPS = 12

function Initialize()
  mRun = SKIN:GetMeasure('MeasureRainRun')
end

local function setOption(meter, option, value)
  local k = meter .. '|' .. option
  if shown[k] == value then return end
  shown[k] = value
  SKIN:Bang('!SetOption', meter, option, value)
  dirty = true
end
local function setText(meter, value) setOption(meter, 'Text', value) end

local function parse(out)
  local t = {}
  for line in (out or ''):gmatch('[^\r\n]+') do
    local k, v = line:match('^(%w+)=(.*)$')
    if k then t[k] = v end
  end
  return t
end

local function clock(t)
  local d = os.date('*t', t)
  if SKIN:GetVariable('ClockFormat', '24') == '12' then
    return string.format('%d:%02d %s', (d.hour + 11) % 12 + 1, d.min, d.hour < 12 and 'AM' or 'PM')
  end
  return string.format('%d:%02d', d.hour, d.min)
end

-- minutes, rounded to 5 ("~20 min"); an hour or more as a time of day ("around 15:30")
local function when(mins, at)
  if mins < 60 then return '~' .. math.max(5, math.floor(mins / 5 + 0.5) * 5) .. ' min', true end
  return clock(at - at % 900), false
end

local function level(mm)
  if mm < LIGHT then return 'Light' elseif mm < MODERATE then return 'Moderate' end
  return 'Heavy'
end

local function bars(vals)
  local ch = tonumber(SKIN:GetVariable('ChartH', '36')) or 36
  for i = 1, STEPS do
    local v, h = vals[i], 0
    -- square root scale: light rain is still easy to see, heavy rain fills the chart
    if v and v >= WET then h = math.max(5, math.floor(ch * math.min(1, math.sqrt(v / 2.5)) + 0.5)) end
    local k = '$RH' .. i
    if shown[k] ~= h then shown[k] = h; SKIN:Bang('!SetVariable', 'RH' .. i, h); dirty = true end
  end
end

function Update()
  local out = mRun and mRun:GetStringValue() or ''
  local w = parse(out)
  local y, mo, d, h, mi = (w.rain or ''):match('^(%d+)-(%d+)-(%d+)T(%d+):(%d+)|')
  if not y then
    setText('MeterName', out == '' and '...' or 'Rain')
    setText('MeterInfo', w.error or (out == '' and '' or 'No rain forecast here'))
    bars({})
  else
    local start = os.time({ year = tonumber(y), month = tonumber(mo), day = tonumber(d), hour = tonumber(h), min = tonumber(mi), sec = 0 })
    local series = {}
    for v in (w.rain:match('|(.*)$') or ''):gmatch('[^,]+') do series[#series + 1] = tonumber(v) or 0 end
    local now = os.time()
    local first = math.floor((now - start) / 900) + 1          -- the step we're in
    local into = ((now - start) % 900) / 60                    -- minutes into it
    local vals = {}
    for i = 1, STEPS do vals[i] = series[first + i - 1] end
    bars(vals)

    local code = tonumber(w.code or '') or 0
    local snow = (code >= 71 and code <= 77) or code == 85 or code == 86
    local word, noun = snow and 'Snowing' or 'Raining', snow and 'snow' or 'rain'
    local known = 0
    for i = 1, STEPS do if vals[i] then known = i end end
    local hours = math.max(1, math.floor(known / 4 + 0.5))

    if known == 0 then
      setText('MeterName', 'Rain')
      setText('MeterInfo', 'Forecast out of date')
    elseif (vals[1] or 0) >= WET then
      local stop
      for i = 2, known do if (vals[i] or 0) < WET then stop = i; break end end
      local total, peak = 0, 0
      for i = 1, (stop or known + 1) - 1 do total = total + vals[i]; peak = math.max(peak, vals[i]) end
      if stop then
        local t, rel = when((stop - 1) * 15 - into, start + (first + stop - 2) * 900)
        setText('MeterName', word .. '  [\\x00B7]  stops ' .. (rel and 'in ' or 'around ') .. t)
      else
        setText('MeterName', word .. ' for the next ' .. hours .. ' h')
      end
      setText('MeterInfo', level(peak) .. ' ' .. noun .. string.format('  [\\x00B7]  %.1f mm to come', total))
    else
      local from
      for i = 2, known do if (vals[i] or 0) >= WET then from = i; break end end
      if from then
        local t, rel = when((from - 1) * 15 - into, start + (first + from - 2) * 900)
        local peak, total = 0, 0
        for i = from, known do
          if (vals[i] or 0) < WET then break end
          peak = math.max(peak, vals[i]); total = total + vals[i]
        end
        setText('MeterName', (snow and 'Snow ' or 'Rain ') .. (rel and 'in ' or 'from about ') .. t)
        setText('MeterInfo', level(peak) .. string.format('  [\\x00B7]  %.1f mm', total))
      else
        setText('MeterName', 'Dry for the next ' .. hours .. ' h')
        setText('MeterInfo', w.time and ('Checked at ' .. w.time) or '')
      end
    end
  end
  if dirty then
    dirty = false
    SKIN:Bang('!UpdateMeter', '*')
    SKIN:Bang('!Redraw')
  end
  return 0
end
