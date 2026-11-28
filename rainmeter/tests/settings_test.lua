-- The Settings panel's keyboard handling (settings.lua Key, called by Loopscape.exe's SettingsKeys.cs) and scrolling

local TABS = { 'Wallpaper', 'Rotation', 'Widgets', 'Look', 'Profiles' }

local function panel(t)
  local skin = t.load('Settings')
  skin:runCode(skin.scripts.measuresettings, "ShowTab('Wallpaper')")
  return skin
end
local function run(skin, code) skin:runCode(skin.scripts.measuresettings, code) end
local function var(skin, name) return skin:variable(name) end
local function num(skin, name) return tonumber(var(skin, name)) or 0 end
local function listOpen(skin) return not skin.meters.ddback:hidden() end

-- The first row whose value opens a list (a setting with more than two options), as its click would
local function openAList(skin)
  for _, sec in ipairs(skin.order) do
    local click = skin:option(sec.name, 'LeftMouseUpAction') or ''
    local var = click:match("Next%('([%w_]+)'%)")
    if var then
      skin:action(click, sec.name)
      if listOpen(skin) then return var end
    end
  end
end

-- A tab taller than the panel, shown
local function tallTab(skin)
  for _, tab in ipairs(TABS) do
    run(skin, "ShowTab('" .. tab .. "')")
    if num(skin, 'ScrollOn') == 1 then return tab end
  end
end

return {
  { 'Ctrl+Tab and Ctrl+Shift+Tab move through the tabs, wrapping around', function(t)
    local skin = panel(t)
    for i = 2, #TABS do
      run(skin, "Key('ctrl+tab')")
      t.check(var(skin, 'Tab') == TABS[i], 'Ctrl+Tab should show ' .. TABS[i] .. ', shows ' .. tostring(var(skin, 'Tab')))
    end
    run(skin, "Key('ctrl+tab')")
    t.check(var(skin, 'Tab') == TABS[1], 'Ctrl+Tab on the last tab should wrap to the first')
    run(skin, "Key('ctrl+shift+tab')")
    t.check(var(skin, 'Tab') == TABS[#TABS], 'Ctrl+Shift+Tab on the first tab should wrap to the last')
  end },

  { 'at least one tab is taller than the panel and gets a scroll bar', function(t)
    t.check(tallTab(panel(t)) ~= nil, 'no tab scrolls, so the scrolling tests below test nothing')
  end },

  { 'the arrows, Page Up/Down, Home and End scroll within the tab', function(t)
    local skin = panel(t)
    if not tallTab(skin) then return end
    t.check(num(skin, 'ScrollY') == 0, 'a tab should open at its top')
    run(skin, "Key('down')")
    local row = num(skin, 'ScrollY')
    t.check(row > 0, 'Down should scroll down')
    run(skin, "Key('up')")
    t.check(num(skin, 'ScrollY') == 0, 'Up should scroll back up')
    run(skin, "Key('up')")
    t.check(num(skin, 'ScrollY') == 0, 'Up at the top should stay at the top')
    run(skin, "Key('pagedown')")
    t.check(num(skin, 'ScrollY') > row, 'Page Down should scroll further than a row')
    run(skin, "Key('end')")
    local bottom = num(skin, 'ScrollY')
    run(skin, "Key('down')")
    t.check(num(skin, 'ScrollY') == bottom, 'Down at the bottom should stay at the bottom')
    run(skin, "Key('pageup')"); run(skin, "Key('home')")
    t.check(num(skin, 'ScrollY') == 0, 'Home should scroll to the top')
    run(skin, "Key('ctrl+tab')"); run(skin, "Key('ctrl+shift+tab')")
    t.check(num(skin, 'ScrollY') == 0, 'switching tabs should start the tab at its top')
  end },

  { 'in an open list the arrows move the light, Enter picks and Esc closes', function(t)
    local skin = panel(t)
    local found
    for _, tab in ipairs(TABS) do
      run(skin, "ShowTab('" .. tab .. "')")
      found = openAList(skin)
      if found then break end
    end
    t.check(found ~= nil, 'no row opens a list')
    if not found then return end
    local lit = function()
      for i = 1, 8 do
        if var(skin, 'DDBase' .. i) == var(skin, 'ColorPillHover') then return i end
      end
    end
    run(skin, "Key('home')")
    t.check(lit() == 1, 'Home should light the first item')
    run(skin, "Key('down')")
    t.check(lit() == 2, 'Down should light the second item')
    run(skin, "Key('up')"); run(skin, "Key('up')")
    t.check(lit() == 1, 'Up should stop at the first item')
    t.check(num(skin, 'ScrollY') == 0, 'the keys in an open list should not scroll the rows')
    run(skin, "Key('escape')")
    t.check(not listOpen(skin), 'Esc should close the list')

    openAList(skin)
    run(skin, "Key('end')"); run(skin, "Key('enter')")
    t.check(not listOpen(skin), 'Enter should pick the lit item and close the list')
  end },

  { 'the keys do nothing while a text box is open', function(t)
    local skin = panel(t)
    run(skin, "ShowTab('Widgets')")
    run(skin, "EditCity()")
    local tab = var(skin, 'Tab')
    run(skin, "Key('ctrl+tab')")
    t.check(var(skin, 'Tab') == tab, 'Ctrl+Tab should not switch tabs while typing')
  end },
}
