-- Parses WifiInfo.exe's netsh-style output (run by MeasureNetsh) into the Wi-Fi widget's text and icon.

function Initialize()
  mRun = SKIN:GetMeasure('MeasureNetsh')
  iconDir = SKIN:GetVariable('@') .. 'WiFi\\'
end

local function field(out, name)
  -- lines look like "    Receive rate (Mbps)    : 1201"
  local pat = '\n%s*' .. name:gsub('[%(%)%-]', '%%%0') .. '%s*:%s*([^\r\n]*)'
  local v = ('\n' .. out):match(pat)
  return v and v:gsub('%s+$', '') or nil
end

local function generation(radio, band)
  if not radio then return nil end
  local r = radio:match('802%.11(%a+)') or ''
  local gen = ({ be = 'Wi-Fi 7', ax = 'Wi-Fi 6', ac = 'Wi-Fi 5', n = 'Wi-Fi 4', g = 'Wi-Fi 3', a = 'Wi-Fi 2' })[r]
  if gen == 'Wi-Fi 6' and band and band:match('^6') then gen = 'Wi-Fi 6E' end
  return gen and (gen .. '  (' .. radio .. ')') or radio
end

local function quality(pct)
  if pct >= 80 then return 'Excellent' elseif pct >= 60 then return 'Good'
  elseif pct >= 40 then return 'Fair' else return 'Weak' end
end

local function set(meter, text) SKIN:Bang('!SetOption', meter, 'Text', text) end

function Update()
  local out = mRun:GetStringValue() or ''
  local state = field(out, 'State')
  local ssid = field(out, 'SSID')

  if state ~= 'connected' or not ssid then
    SKIN:Bang('!SetOption', 'MeterIcon', 'ImageName', iconDir .. 'wifi-off.png')
    set('MeterSSID', (state == nil and out == '') and 'Checking...' or 'Not connected')
    set('MeterQuality', state and ('Wi-Fi ' .. state) or 'No Wi-Fi adapter found')
    for i = 1, 5 do set('MeterV' .. i, '-') end
  else
    local pct = tonumber((field(out, 'Signal') or ''):match('(%d+)')) or 0
    local level = pct >= 75 and 3 or pct >= 50 and 2 or pct >= 25 and 1 or 0
    local band, channel = field(out, 'Band'), field(out, 'Channel')
    local rx, tx = field(out, 'Receive rate (Mbps)'), field(out, 'Transmit rate (Mbps)')
    local auth = (field(out, 'Authentication') or ''):gsub('%s*%(.*%)', '')
    local rssi = field(out, 'Rssi')

    SKIN:Bang('!SetOption', 'MeterIcon', 'ImageName', iconDir .. 'wifi-' .. level .. '.png')
    set('MeterSSID', ssid)
    set('MeterQuality', quality(pct) .. '  [\\x00B7]  ' .. pct .. '%')
    set('MeterV1', (band or '?') .. (channel and ('  [\\x00B7]  channel ' .. channel) or ''))
    set('MeterV2', rx and (rx == tx and (rx .. ' Mbps') or (rx .. ' down / ' .. (tx or '?') .. ' up Mbps')) or '-')
    set('MeterV3', generation(field(out, 'Radio type'), band) or '-')
    set('MeterV4', auth ~= '' and auth or 'Open')
    set('MeterV5', rssi and (rssi .. ' dBm') or (pct .. '%'))
  end
  -- the meters only update when asked (DefaultUpdateDivider=-1): two passes, because the panel and the pills come
  -- before the text they wrap
  SKIN:Bang('!UpdateMeter', '*')
  SKIN:Bang('!UpdateMeter', '*')
  SKIN:Bang('!Redraw')
  return ssid or ''
end
