-- Builds the battery line for Clock.ini, e.g. "87%  |  3h 12m left", and decides whether the line is shown.
-- ShowBattery=auto shows it only when Windows reports a battery, so desktops don't get a "No battery" line.

function Initialize()
  mAC     = SKIN:GetMeasure('MeasureACLine')
  mStatus = SKIN:GetMeasure('MeasureStatus')
  mPct    = SKIN:GetMeasure('MeasurePercent')
  mLife   = SKIN:GetMeasure('MeasureLifetime')
  low     = tonumber(SKIN:GetVariable('LowBattery')) or 20
  colSub  = SKIN:GetVariable('ColorSub')
  colWarn = SKIN:GetVariable('ColorWarn')
  lastCol = nil
  shown   = nil
end

-- Shows or hides the battery line. This measure runs before the meters, so they and the layout formulas
-- (DateBlockBottom) read the new BatteryShown in the same update. A later switch always changes the text too
-- ('' while hidden), so MeasureBatteryText's OnChangeAction does the two-pass redraw.
-- DateBlockBottom is set here too: Rainmeter fills in the #BatteryShown# inside its definition once, when the skin
-- loads (BatteryShown=0), so changing BatteryShown alone left the forecast row under the date, over the battery line.
local function setShown(v)
  if v ~= shown then
    SKIN:Bang('!SetVariable', 'BatteryShown', v and 1 or 0)
    SKIN:Bang('!SetVariable', 'DateBlockBottom',
      v and '([MeterBattery:Y]+[MeterBattery:H])' or '([MeterDate:Y]+[MeterDate:H])')
    shown = v
  end
end

local function wantLine(hasBattery)
  local mode = (SKIN:GetVariable('ShowBattery', 'auto') or 'auto'):lower()
  if mode == '1' then return true end
  if mode == '0' then return false end
  return hasBattery
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
  -- PowerPlugin Status: 0 = no battery (a desktop, or Windows can't read the battery)
  local hasBattery = mStatus:GetValue() ~= 0
  setShown(wantLine(hasBattery))
  if not shown then
    return ''
  end
  if not hasBattery then
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
