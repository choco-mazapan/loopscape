-- Turns WeatherFetch.exe output (key=value lines) into the widget's weather icon, temperature and description.
-- Open-Meteo uses WMO weather codes: https://open-meteo.com/en/docs (see "WMO Weather interpretation codes")

function Initialize()
  mRun = SKIN:GetMeasure('MeasureWeatherRun')
  iconDir = SKIN:GetVariable('@') .. 'Weather\\'
end

-- Changing a tooltip at runtime can leave old tooltip windows stuck on screen, so only touch ones whose text changed
local tips = {}
local function setTip(meter, text)
  if tips[meter] == text then return end
  tips[meter] = text
  SKIN:Bang('!SetOption', meter, 'ToolTipText', text)
end

-- The clock's meters only update when asked (DefaultUpdateDivider=-1), so update all of them: the panel, the pills
-- and the stack width follow the weather text. Two passes because the panel and pills come before the text they wrap.
local function redraw()
  SKIN:Bang('!UpdateMeter', '*')
  SKIN:Bang('!UpdateMeter', '*')
  SKIN:Bang('!Redraw')
end

local function classify(code, day)
  if code == 0 then return (day and 'clear-day' or 'clear-night'), 'Clear' end
  if code == 1 then return (day and 'clear-day' or 'clear-night'), 'Mostly clear' end
  if code == 2 then return (day and 'partly-day' or 'partly-night'), 'Partly cloudy' end
  if code == 3 then return 'cloudy', 'Overcast' end
  if code == 45 or code == 48 then return 'fog', 'Fog' end
  if code >= 51 and code <= 57 then return 'drizzle', 'Drizzle' end
  if code >= 61 and code <= 67 then return 'rain', (code >= 65 and 'Heavy rain' or 'Rain') end
  if code >= 71 and code <= 77 then return 'snow', 'Snow' end
  if code >= 80 and code <= 82 then return 'rain', 'Showers' end
  if code == 85 or code == 86 then return 'snow', 'Snow showers' end
  if code >= 95 then return 'thunder', 'Thunderstorm' end
  return 'cloudy', 'Cloudy'
end

-- Sunny icons are colour-coded by temperature too: hot (from 29 C / 84 F) red-orange, mild amber, cool (below 13 C / 55 F) pale
local function sunVariant(icon, temp)
  if icon ~= 'clear-day' or not temp then return icon end
  local c = (SKIN:GetVariable('WeatherUnit', 'fahrenheit') == 'celsius') and temp or (temp - 32) * 5 / 9
  if c >= 29 then return 'clear-day-hot' elseif c < 13 then return 'clear-day-cool' end
  return icon
end
-- #WxShade_<meter># (light / deep) is set by snap.lua from the background, so the path stays a variable
local function iconPath(meter, sub, icon)
  return iconDir .. '#*WxShade_' .. meter .. '*#\\' .. sub .. icon .. '.png'
end

function Update()
  local out = mRun:GetStringValue() or ''
  local t = {}
  for k, v in out:gmatch('(%w+)=([^\r\n]*)') do t[k] = v end
  if not t.code or not t.temp then
    -- No city set, city not found, Windows location off: say so instead of a blank widget.
    -- Other failures (offline, etc.) keep showing the last good reading.
    local e = t.error or ''
    if lastDesc and not e:match('[Cc]ity') and not e:match('location') then return lastDesc end
    if e ~= '' then
      SKIN:Bang('!SetOption', 'MeasureWeatherTemp', 'String', '--')
      SKIN:Bang('!UpdateMeasure', 'MeasureWeatherTemp')
      SKIN:Bang('!SetOption', 'MeterWeatherDesc', 'Text', e)
      SKIN:Bang('!SetOption', 'MeterWeatherCity', 'Text', '')
      redraw()
      lastDesc = nil
      return e
    end
    return lastDesc or ''
  end

  local icon, desc = classify(tonumber(t.code), t.isday == '1')
  local temp = math.floor(tonumber(t.temp) + 0.5)
  local where = (t.city and t.city ~= '') and (t.city .. ', ') or ''
  local via = (t.source == 'city') and 'your city in Settings'
           or (t.source == 'windows') and 'Windows location'
           or 'recent Windows location'

  SKIN:Bang('!SetOption', 'MeasureWeatherTemp', 'String', tostring(temp))
  SKIN:Bang('!UpdateMeasure', 'MeasureWeatherTemp')
  SKIN:Bang('!SetOption', 'MeterWeatherIcon', 'ImageName', iconPath('MeterWeatherIcon', '', sunVariant(icon, temp)))
  SKIN:Bang('!SetOption', 'MeterWeatherDesc', 'Text', desc)
  SKIN:Bang('!SetOption', 'MeterWeatherCity', 'Text', t.city or '')
  -- (no "updated" time in the tooltip: it would change the tooltip on every fetch)
  for _, m in ipairs({ 'MeterWeatherIcon', 'MeterWeatherTemp', 'MeterWeatherDesc', 'MeterWeatherCity' }) do
    setTip(m, desc .. ' - ' .. where .. 'via ' .. via)
  end

  -- 3-day forecast: fcN=Day|code|max|min
  local shown = 0
  for i = 1, 3 do
    local day, code, hi, lo = (t['fc' .. i] or ''):match('^([^|]+)|(%d+)|([^|]+)|([^|]+)$')
    if day then
      local ficon, fdesc = classify(tonumber(code), true)
      SKIN:Bang('!SetOption', 'MeterFcIcon' .. i, 'ImageName', iconPath('MeterFcIcon' .. i, 'small\\', sunVariant(ficon, tonumber(hi))))
      SKIN:Bang('!SetOption', 'MeterFcDay' .. i, 'Text', day)
      SKIN:Bang('!SetOption', 'MeterFcTemp' .. i, 'Text', hi .. '[\\x00B0] ' .. lo .. '[\\x00B0]')
      for _, m in ipairs({ 'MeterFcIcon', 'MeterFcDay', 'MeterFcTemp' }) do
        setTip(m .. i, day .. ': ' .. fdesc .. ', high ' .. hi .. ' / low ' .. lo)
      end
      shown = shown + 1
    end
  end
  -- (visibility per view is handled by each meter's Hidden=(#View#>=n) option)
  redraw()
  lastDesc = desc
  return desc
end
