-- Parses WifiInfo.exe's netsh-style output (run by MeasureNetsh) into the Network widget's text and icon.
-- WifiInfo reports Wi-Fi, or a wired connection ("Connection : Ethernet") when the computer uses one; the widget then
-- shows the adapter's details in place of the Wi-Fi ones, with row labels to match.

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

-- Labels of the five detail rows (MeterL1..L5) for each kind of connection
local WIFI_LABELS = { 'Band', 'Link speed', 'Standard', 'Security', 'Signal' }
local WIRED_LABELS = { 'Adapter', 'Link speed', 'Gateway', 'DNS server', 'MAC address' }
local shownLabels

local function setLabels(labels)
  if labels == shownLabels then return end
  shownLabels = labels
  for i = 1, 5 do set('MeterL' .. i, labels[i]) end
end

local function speedText(mbps)
  if not mbps then return nil end
  if mbps < 1000 then return mbps .. ' Mbps' end
  local g = mbps / 1000
  return (g == math.floor(g) and string.format('%d', g) or string.format('%.1f', g)) .. ' Gbps'
end

-- Adapter names like "Realtek PCIe GbE Family Controller" or "Intel(R) Ethernet Controller I225-V": drop the
-- trademark marks and "Family Controller", and cut what's left so the row doesn't widen the widget much
local MAX_ADAPTER = 26
local function adapterText(desc)
  if not desc then return nil end
  desc = desc:gsub('%(R%)', ''):gsub('%(TM%)', ''):gsub('%s+Family%s+Controller', ''):gsub('%s+', ' ')
  if #desc > MAX_ADAPTER then desc = desc:sub(1, MAX_ADAPTER - 1):gsub('%s+$', '') .. '[\\x2026]' end
  return desc
end

local function showWired(out)
  local mbps = tonumber(field(out, 'Link speed (Mbps)') or '')
  SKIN:Bang('!SetOption', 'MeterIcon', 'ImageName', iconDir .. 'ethernet.png')
  setLabels(WIRED_LABELS)
  set('MeterSSID', field(out, 'Name') or 'Ethernet')
  set('MeterQuality', 'Wired' .. (mbps and ('  [\\x00B7]  ' .. speedText(mbps)) or ''))
  set('MeterV1', adapterText(field(out, 'Description')) or '-')
  set('MeterV2', speedText(mbps) or '-')
  set('MeterV3', field(out, 'Gateway') or '-')
  set('MeterV4', field(out, 'DNS server') or '-')
  set('MeterV5', field(out, 'Physical address') or '-')
end

local function showWifi(out, state, ssid)
  setLabels(WIFI_LABELS)
  if state ~= 'connected' or not ssid then
    SKIN:Bang('!SetOption', 'MeterIcon', 'ImageName', iconDir .. 'wifi-off.png')
    if out == '' then
      set('MeterSSID', 'Checking...')
      set('MeterQuality', '')
    else
      set('MeterSSID', 'Not connected')
      -- with no State line there is no Wi-Fi adapter, and WifiInfo found no wired connection either
      set('MeterQuality', state and ('Wi-Fi ' .. state) or 'No network connection')
    end
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
end

function Update()
  local out = mRun:GetStringValue() or ''
  local name
  if field(out, 'Connection') == 'Ethernet' then
    showWired(out)
    name = field(out, 'Name') or 'Ethernet'
  else
    local state, ssid = field(out, 'State'), field(out, 'SSID')
    showWifi(out, state, ssid)
    name = ssid
  end
  -- the meters only update when asked (DefaultUpdateDivider=-1): two passes, because the panel and the pills come
  -- before the text they wrap
  SKIN:Bang('!UpdateMeter', '*')
  SKIN:Bang('!UpdateMeter', '*')
  SKIN:Bang('!Redraw')
  return name or ''
end
