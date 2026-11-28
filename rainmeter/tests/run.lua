-- Runs the Loopscape skins' Lua scripts against a stand-in for Rainmeter (fake_rainmeter.lua) and reports what would
-- break in Rainmeter: Lua errors, unknown or malformed bangs, formulas that don't evaluate.
--
--   lua5.1 rainmeter/tests/run.lua
--
-- It copies rainmeter/Loopscape into a temporary folder and works there, so the scripts' file writes (Settings.inc,
-- profiles, wallcmd.txt) never touch the repository. For each skin it:
--   1. loads the .ini and its includes, runs each script's Initialize and Update, and evaluates every meter's
--      position and size
--   2. carries out every action in the file (mouse, context menu, scroll ...) in order, ticking Update now and then,
--      which runs each !CommandMeasure into the skin's own scripts
-- Then it runs the tests in *_test.lua next to this file. Exits with 1 if anything failed.

local here = (arg and arg[0] or ''):match('^(.*)[/\\]') or '.'
package.path = here .. '/?.lua;' .. package.path
local fake = require('fake_rainmeter')

local function sh(cmd)
  local ok = os.execute(cmd)
  return ok == 0 or ok == true
end
local function readAll(cmd)
  local p = io.popen(cmd); local s = p:read('*a'); p:close(); return s
end

-- ---------- the sandbox ----------
local sandbox = readAll('mktemp -d'):gsub('%s+$', '')
assert(sandbox ~= '' and sandbox:match('^/'), 'mktemp failed')
local skins = sandbox .. '/Skins/'
local source = here .. '/../Loopscape'
assert(sh(string.format("mkdir -p '%s/Local/Loopscape' '%s/Home/Loopscape/styles' '%s/Roaming/Rainmeter' '%s'",
  sandbox, sandbox, sandbox, skins)), 'could not make the sandbox')
assert(sh(string.format("cp -R '%s' '%sLoopscape'", source, skins)), 'could not copy the skins')

-- The skins, as "Folder/File.ini" under Loopscape
local list = {}
for line in readAll(string.format("cd '%sLoopscape' && find . -mindepth 2 -maxdepth 2 -name '*.ini' | sort", skins)):gmatch('[^\n]+') do
  local folder, file = line:match('^%./([^/]+)/([^/]+)$')
  if folder and folder ~= '@Resources' then list[#list + 1] = { folder = folder, file = file } end
end

-- ---------- running ----------
local failures, checks = {}, 0
local function report(skin)
  local seen = {}
  for _, p in ipairs(skin.problems) do
    if not seen[p] then seen[p] = true; failures[#failures + 1] = p end
  end
  skin.problems = {}
end

local function load(folder, file)
  return fake.load({
    sandbox = sandbox, skins = skins, path = skins .. 'Loopscape/' .. folder .. '/' .. (file or (folder .. '.ini')),
    config = 'Loopscape\\' .. folder,
  })
end

for _, s in ipairs(list) do
  local skin = load(s.folder, s.file)
  skin:start()
  skin:measureAll()
  skin:tick(); skin:tick()
  checks = checks + 1
  local actions = skin:actions()
  for i, a in ipairs(actions) do
    skin:action(a[3], a[1] .. ' ' .. a[2])
    if i % 10 == 0 then skin:tick() end
  end
  skin:tick()
  skin:measureAll()
  checks = checks + #actions
  report(skin)
  print(string.format('%-26s %3d action(s), %3d script command(s)', s.folder .. '/' .. s.file, #actions, skin.commands or 0))
end

-- ---------- the tests next to this file ----------
-- Each *_test.lua returns a list of { name, function(t) }; t.load(folder) gives a started skin, t.check(cond, msg)
-- records a failure
local tests = readAll(string.format("ls '%s' | grep '_test[.]lua$' | sort", here))
for file in tests:gmatch('[^\n]+') do
  local cases = dofile(here .. '/' .. file)
  for _, case in ipairs(cases) do
    local name, fn = case[1], case[2]
    local loaded = {}
    local t = {
      load = function(folder, ini)
        local skin = load(folder, ini):start()
        loaded[#loaded + 1] = skin
        return skin
      end,
      check = function(cond, msg)
        checks = checks + 1
        if not cond then failures[#failures + 1] = file .. ': ' .. name .. ': ' .. msg end
      end,
    }
    local ok, err = xpcall(function() fn(t) end, debug.traceback)
    if not ok then failures[#failures + 1] = file .. ': ' .. name .. ': ' .. tostring(err) end
    for _, skin in ipairs(loaded) do
      for _, p in ipairs(skin.problems) do failures[#failures + 1] = file .. ': ' .. name .. ': ' .. p end
    end
    print(string.format('%-24s %s', file, name))
  end
end

sh(string.format("rm -rf '%s'", sandbox))
if #failures > 0 then
  for _, f in ipairs(failures) do print('::error::' .. f:gsub('\n', ' | ')) end
  print(#failures .. ' failure(s)')
  os.exit(1)
end
print('All ' .. checks .. ' checks passed.')
