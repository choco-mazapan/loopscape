-- Fills the view menu from Target/Names/Current (set by the widget that opened it) and applies a pick.
local MAX = 6

function Initialize() end
function Update() return 0 end

function Build()
  local names = {}
  for n in (SKIN:GetVariable('Names', '')):gmatch('[^|]+') do names[#names + 1] = n end
  local current = (tonumber(SKIN:GetVariable('Current', '0')) or 0) + 1
  SKIN:Bang('!SetVariable', 'Count', math.min(#names, MAX))
  for i = 1, MAX do
    local m = 'MeterItem' .. i
    if names[i] then
      local base = (i == current) and SKIN:GetVariable('ColorCurrent') or SKIN:GetVariable('ColorItem')
      SKIN:Bang('!SetVariable', 'Base' .. i, base)
      SKIN:Bang('!SetOption', m, 'Text', names[i])
      SKIN:Bang('!SetOption', m, 'SolidColor', base)
      SKIN:Bang('!ShowMeter', m)
    else
      SKIN:Bang('!HideMeter', m)
    end
  end
  SKIN:Bang('!UpdateMeter', '*')
  SKIN:Bang('!Redraw')
end

function Pick(i)
  SKIN:Bang('!CommandMeasure', 'MeasureSnap', 'SetView(' .. (i - 1) .. ')', SKIN:GetVariable('Target'))
  SKIN:Bang('!DeactivateConfig')
end