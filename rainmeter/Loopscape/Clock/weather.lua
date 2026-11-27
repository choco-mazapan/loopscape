-- Turns WeatherFetch.exe output (key=value lines) into the widget's weather icon, temperature and description.
-- Open-Meteo uses WMO weather codes: https://open-meteo.com/en/docs (see "WMO Weather interpretation codes")

function Initialize()
  mRun = SKIN:GetMeasure('MeasureWeatherRun')
  iconDir = SKIN:GetVariable('@') .. 'Weather\\'
end

-- Files Loopscape.exe shares while it runs (wallstatus.txt says when a window covers the desktop)
local STATE = (os.getenv('LOCALAPPDATA') or ((os.getenv('USERPROFILE') or '') .. '\\AppData\\Local')) .. '\\Loopscape\\'

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
  SKIN:Bang('!UpdateMeter', 'Pill_MeterAmPm')   -- the AM/PM text can move on the second pass, after its pill
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
-- Sets a weather icon's image, and the same image on its outline / shadow copies (Halo_<meter>_1..9 in Clock.ini)
local function setIcon(meter, path)
  SKIN:Bang('!SetOption', meter, 'ImageName', path)
  for i = 1, 9 do SKIN:Bang('!SetOption', 'Halo_' .. meter .. '_' .. i, 'ImageName', path) end
end

-- ---------- animated weather icon (Settings > Animate weather icons) ----------
-- Only the large icon animates. Weather\<shade>\anim\<icon>.png holds FRAMES frames side by side; MeasureWxAnim calls
-- Frame() every WxFrameMs, which moves the crop (#WxCrop#) along the strip and redraws. Each frame costs one redraw
-- of the clock, so the frames are few and slow, and Frame() skips the redraw when nobody can see the icon.
local FRAMES = 6
local mainIcon = nil   -- the large icon's name (e.g. 'rain'), once the weather is in
local animOn = false
local frame = 0
local covered, coveredCheck = false, 0

local function animating() return SKIN:GetVariable('AnimateWeather', '0') == '1' end

-- Shows the large icon still or animated, to match AnimateWeather
local function showMainIcon()
  if not mainIcon then return end
  local cell = tonumber(SKIN:GetVariable('WxStripCell', '48')) or 48
  if animOn then
    setIcon('MeterWeatherIcon', iconPath('MeterWeatherIcon', 'anim\\', mainIcon))
    SKIN:Bang('!SetVariable', 'WxCrop', (frame * cell) .. ',0,' .. cell .. ',' .. cell)
  else
    setIcon('MeterWeatherIcon', iconPath('MeterWeatherIcon', '', mainIcon))
    SKIN:Bang('!SetVariable', 'WxCrop', '')
  end
end

-- Starts or stops the animation after AnimateWeather changed (Settings) or the widget loaded (OnRefreshAction)
function Animate()
  local on = animating()
  if on ~= animOn then
    animOn = on
    frame = 0
    SKIN:Bang('!CommandMeasure', 'MeasureWxAnim', on and 'Execute 1' or 'Stop 1')
    if not on then SKIN:Bang('!CommandMeasure', 'MeasureWxAnim', 'Stop 2') end
  end
  showMainIcon()
  SKIN:Bang('!UpdateMeterGroup', 'WxAnim')
  SKIN:Bang('!Redraw')
end

-- One animation step. Skipped (no redraw) while the icon is hidden (Compact and Time only views) or while a window
-- covers the desktop, which Loopscape notes in wallstatus.txt (checked every few seconds).
function Frame()
  if not animOn or not mainIcon then return end
  if tonumber(SKIN:GetVariable('View', '0')) >= 2 then return end
  coveredCheck = coveredCheck - 1
  if coveredCheck <= 0 then
    coveredCheck = 10
    local f = io.open(STATE .. 'wallstatus.txt', 'r')
    local text = f and f:read('*a') or ''
    if f then f:close() end
    covered = text:find('window covers the desktop', 1, true) ~= nil
  end
  if covered then return end
  frame = (frame + 1) % FRAMES
  local cell = tonumber(SKIN:GetVariable('WxStripCell', '48')) or 48
  SKIN:Bang('!SetVariable', 'WxCrop', (frame * cell) .. ',0,' .. cell .. ',' .. cell)
  SKIN:Bang('!UpdateMeterGroup', 'WxAnim')
  SKIN:Bang('!Redraw')
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
  mainIcon = sunVariant(icon, temp)
  showMainIcon()
  SKIN:Bang('!SetOption', 'MeterWeatherDesc', 'Text', desc)
  SKIN:Bang('!SetOption', 'MeterWeatherCity', 'Text', t.city or '')
  -- (no "updated" time in the tooltip: it would change the tooltip on every fetch)
  -- One tooltip area per block (see the tooltip areas in Clock.ini): a tooltip on each meter opened several at once
  setTip('MeterWeatherTip', desc .. ' - ' .. where .. 'via ' .. via)

  -- 3-day forecast: fcN=Day|code|max|min
  local shown = 0
  for i = 1, 3 do
    local day, code, hi, lo = (t['fc' .. i] or ''):match('^([^|]+)|(%d+)|([^|]+)|([^|]+)$')
    if day then
      local ficon, fdesc = classify(tonumber(code), true)
      setIcon('MeterFcIcon' .. i, iconPath('MeterFcIcon' .. i, 'small\\', sunVariant(ficon, tonumber(hi))))
      SKIN:Bang('!SetOption', 'MeterFcDay' .. i, 'Text', day)
      SKIN:Bang('!SetOption', 'MeterFcTemp' .. i, 'Text', hi .. '[\\x00B0] ' .. lo .. '[\\x00B0]')
      setTip('MeterFcTip' .. i, day .. ': ' .. fdesc .. ', high ' .. hi .. ' / low ' .. lo)
      shown = shown + 1
    end
  end
  -- (visibility per view is handled by each meter's Hidden=(#View#>=n) option)
  redraw()
  lastDesc = desc
  return desc
end
