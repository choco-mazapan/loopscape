-- The Settings panel's keyboard handling (settings.lua Key, called by Loopscape.exe's SettingsKeys.cs): scrolling, the
-- open list's keys, and the outline Tab moves from button to button

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

-- The meter the focus outline is drawn around (3 px outside it), found by its position
local function focused(skin)
  if num(skin, 'FocusOn') ~= 1 then return nil end
  local x, y = num(skin, 'FocusX') + 3, num(skin, 'FocusY') + 3
  local group = 'Tab' .. var(skin, 'Tab')
  for _, sec in ipairs(skin.order) do
    local m = skin.meters[sec.name:lower()]
    if m and m:GetOption('Group') == group and (skin:option(sec.name, 'LeftMouseUpAction') or '') ~= ''
        and m:GetX() == x and m:GetY() == y then
      return sec.name
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

  { 'Tab and Shift+Tab move the outline through every button and tile in reading order, wrapping around', function(t)
    local skin = panel(t)
    for _, tab in ipairs(TABS) do
      run(skin, "ShowTab('" .. tab .. "')")
      t.check(num(skin, 'FocusOn') == 0, tab .. ': a tab should open with nothing outlined')
      local seen, order, lastY = {}, {}, -1
      for _ = 1, 60 do
        run(skin, "Key('tab')")
        local name = focused(skin)
        t.check(name ~= nil, tab .. ': after Tab the outline should be around a button or tile')
        if not name or seen[name] then break end
        seen[name] = true; order[#order + 1] = name
        local y = skin.meters[name:lower()]:GetY()
        t.check(y >= lastY, tab .. ': Tab should go top to bottom, but ' .. name .. ' is above the one before it')
        lastY = y
      end
      t.check(#order > 0, tab .. ': Tab outlines nothing')
      t.check(focused(skin) == order[1], tab .. ': Tab after the last one should wrap to the first')
      run(skin, "Key('shift+tab')")
      t.check(focused(skin) == order[#order], tab .. ': Shift+Tab from the first should wrap to the last')
      run(skin, "Key('shift+tab')")
      if #order > 1 then t.check(focused(skin) == order[#order - 1], tab .. ': Shift+Tab should go back one') end
      run(skin, "Key('escape')")
      t.check(num(skin, 'FocusOn') == 0, tab .. ': Esc should take the outline away')
    end
  end },

  { 'Enter and Space do what a click on the outlined button does', function(t)
    local skin = panel(t)
    local tested = 0
    for _, tab in ipairs(TABS) do
      run(skin, "ShowTab('" .. tab .. "')")
      for _ = 1, 60 do
        run(skin, "Key('tab')")
        local name = focused(skin)
        local v = name and (skin:option(name, 'LeftMouseUpAction') or ''):match("Next%('([%w_]+)'%)")
        if v then
          local before = var(skin, v)
          run(skin, "Key('space')")
          if listOpen(skin) then
            run(skin, "Key('escape')")
            t.check(not listOpen(skin), tab .. ': Esc should close the list Space opened')
          else
            t.check(var(skin, v) ~= before, tab .. ': Space on ' .. name .. ' should change ' .. v)
            run(skin, "Key('enter')")
            t.check(var(skin, v) == before, tab .. ': Enter on ' .. name .. ' should change ' .. v .. ' back')
          end
          t.check(focused(skin) == name, tab .. ': the outline should stay on ' .. name)
          tested = tested + 1
          break
        end
      end
    end
    t.check(tested >= 3, 'only ' .. tested .. ' tab(s) have a setting to try Enter and Space on')
  end },

  { 'the outline scrolls into view, and switching tabs or opening a list leaves it where it should be', function(t)
    local skin = panel(t)
    if not tallTab(skin) then return end
    run(skin, "Key('shift+tab')")
    local y, h = num(skin, 'FocusY') - num(skin, 'ScrollY'), num(skin, 'FocusH')
    t.check(num(skin, 'ScrollY') > 0, 'Shift+Tab to the last button should scroll down to it')
    t.check(y + h <= skin:formula('(#ViewBottom#)', 'test'), 'the last button should be above the bottom of the view')
    run(skin, "Key('tab')")
    t.check(num(skin, 'ScrollY') == 0 or num(skin, 'FocusY') - num(skin, 'ScrollY') >= skin:formula('(#ViewTop#)', 'test'),
      'Tab back to the first button should scroll up to it')
    run(skin, "Key('ctrl+tab')")
    t.check(num(skin, 'FocusOn') == 0, 'switching tabs should take the outline away')

    for _, tab in ipairs(TABS) do
      run(skin, "ShowTab('" .. tab .. "')")
      if openAList(skin) then break end
    end
    if listOpen(skin) then
      run(skin, "Key('tab')")
      t.check(not listOpen(skin), 'Tab should close an open list')
      t.check(focused(skin) ~= nil, 'Tab with a list open should still outline a button')
    end
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
