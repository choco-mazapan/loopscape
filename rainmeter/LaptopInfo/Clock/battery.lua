-- Builds the battery line for Clock.ini, e.g. "87%  |  3h 12m left"

function Initialize()
  mAC     = SKIN:GetMeasure('MeasureACLine')
  mStatus = SKIN:GetMeasure('MeasureStatus')
  mPct    = SKIN:GetMeasure('MeasurePercent')
  mLife   = SKIN:GetMeasure('MeasureLifetime')
  low     = tonumber(SKIN:GetVariable('LowBattery')) or 20
  colSub  = SKIN:GetVariable('ColorSub')
  colWarn = SKIN:GetVariable('ColorWarn')
  lastCol = nil
end

local function setColor(c)
  -- colours can change at runtime (adaptive text), so re-read them
  -- (ColorBattery is this line's own colour in per-line mode, set by snap.lua)
  if c == colSub then c = SKIN:GetVariable('ColorBattery', SKIN:GetVariable('ColorSub')) elseif c == colWarn then c = SKIN:GetVariable('ColorWarn') end
  if c ~= lastCol then
    SKIN:Bang('!SetOption', 'MeterBattery', 'FontColor', c)
    lastCol = c
  end
end

function Update()
  -- PowerPlugin Status: 0 = no battery
  if mStatus:GetValue() == 0 then
    setColor(colSub)
    return 'No battery'
  end

  local pct = math.floor(mPct:GetValue() + 0.5)
  local onAC = mAC:GetValue() == 1

  if onAC then
    setColor(colSub)
    if pct >= 100 then
      return pct .. '%  |  Plugged in'
    end
    return pct .. '%  |  Charging'
  end

  setColor(pct <= low and colWarn or colSub)

  -- Lifetime is in seconds; Windows reports -1/unknown right after unplugging
  local s = mLife:GetValue()
  if s == nil or s <= 0 or s > 2 * 86400 then
    return pct .. '%  |  Estimating...'
  end

  local h = math.floor(s / 3600)
  local m = math.floor((s % 3600) / 60)
  return string.format('%d%%  |  %dh %02dm left', pct, h, m)
end
