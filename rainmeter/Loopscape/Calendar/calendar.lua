-- Calendar widget: fills the 6 x 7 grid of day meters (MeterD1..MeterD42) for the month shown, the weekday letters
-- (MeterH1..MeterH7, in the order Windows starts the week) and today's ring; the Simple view's date and ISO week.
-- Shift(n) moves n months (scrolling), Shift(0) comes back to this month.

local offset = 0      -- months away from this one
local shown = {}
local dirty = false

local MONTHS = { 'January', 'February', 'March', 'April', 'May', 'June', 'July', 'August', 'September', 'October',
                 'November', 'December' }
local DAYS = { 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday' }
local LETTERS = { 'M', 'T', 'W', 'T', 'F', 'S', 'S' }

local function setText(meter, value)
  if shown[meter] == value then return end
  shown[meter] = value
  SKIN:Bang('!SetOption', meter, 'Text', value)
  dirty = true
end
local function setVar(name, value)
  value = tostring(value)
  if shown['$' .. name] == value then return end
  shown['$' .. name] = value
  SKIN:Bang('!SetVariable', name, value)
  dirty = true
end

-- os.date's wday is 1 Sunday ... 7 Saturday; here 0 Monday ... 6 Sunday (as Windows' iFirstDayOfWeek)
local function monday0(wday) return (wday + 5) % 7 end

local function daysIn(year, month)
  return os.date('*t', os.time({ year = year, month = month + 1, day = 0, hour = 12 })).day
end

-- ISO 8601 week: the week (Monday to Sunday) belongs to the year its Thursday is in
local function isoWeek(t)
  local thursday = os.date('*t', os.time({ year = t.year, month = t.month, day = t.day - monday0(t.wday) + 3, hour = 12 }))
  return math.floor((thursday.yday - 1) / 7) + 1
end

local function draw()
  local m = SKIN:GetMeasure('MeasureFirstDay')
  local first = math.floor(tonumber(m and m:GetValue() or 0) or 0) % 7
  local today = os.date('*t')

  -- month shown
  local mm = today.month - 1 + offset
  local year, month = today.year + math.floor(mm / 12), mm % 12 + 1
  setText('MeterHead', MONTHS[month] .. ' ' .. year)
  for c = 1, 7 do setText('MeterH' .. c, LETTERS[(first + c - 1) % 7 + 1]) end

  local start = os.date('*t', os.time({ year = year, month = month, day = 1, hour = 12 }))
  local lead = (monday0(start.wday) - first) % 7     -- empty cells before the 1st
  local count = daysIn(year, month)
  for n = 1, 42 do
    local d = n - lead
    setText('MeterD' .. n, (d >= 1 and d <= count) and tostring(d) or '')
  end
  setVar('Rows', math.ceil((lead + count) / 7))

  if year == today.year and month == today.month then
    local cell = lead + today.day - 1
    setVar('TodayCol', cell % 7); setVar('TodayRow', math.floor(cell / 7)); setVar('TodayOn', 1)
  else
    setVar('TodayOn', 0)
  end

  -- Simple view: "Tuesday 24" / "November 2026  ·  week 48"
  setText('MeterName', DAYS[monday0(today.wday) + 1] .. ' ' .. today.day)
  setText('MeterInfo', MONTHS[today.month] .. ' ' .. today.year .. '  [\\x00B7]  week ' .. isoWeek(today))

  if dirty then
    dirty = false
    SKIN:Bang('!UpdateMeter', '*')
    SKIN:Bang('!Redraw')
  end
end

function Update()
  draw()
  return 0
end

function Shift(n)
  n = tonumber(n) or 0
  if n == 0 then offset = 0 else offset = offset + n end
  draw()
  -- the number of weeks may have changed: tell the stack now instead of on the next tick
  if SKIN:GetMeasure('MeasureSnap') then SKIN:Bang('!UpdateMeasure', 'MeasureSnap') end
end
