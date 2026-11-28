-- A stand-in for Rainmeter, enough to run the Loopscape skins' Lua scripts outside Windows (see run.lua).
--
-- It reads a skin's .ini the way Rainmeter does ([Variables] in order, @Include files where they are included,
-- MeterStyle), gives each Script measure its SKIN and SELF objects, evaluates Rainmeter formulas, and carries out the
-- bangs that change what a script can see: variables, options, shown and hidden meters, and !CommandMeasure into the
-- skin's own scripts. Other bangs are only checked. Anything Rainmeter would reject or log as an error is recorded in
-- skin.problems: an unknown bang, a bang with too few arguments, a nil argument, a formula that doesn't evaluate, and
-- any Lua error.
--
-- Files: the scripts build Windows paths ("...\Loopscape\wallcmd.txt"). io.open and os.remove see them with '/' in
-- place of '\', and os.getenv points LOCALAPPDATA and USERPROFILE into the sandbox folder, so nothing outside it is
-- read or written.

local M = {}

-- ---------- small helpers ----------
local function lower(s) return s:lower() end
local function trim(s) return (s:gsub('^%s+', ''):gsub('%s+$', '')) end
local function unixPath(p) return (p:gsub('\\', '/'):gsub('//+', '/')) end

-- ---------- formulas ----------
-- Rainmeter's formula language: numbers, + - * / % ** (power), comparisons = <> < > <= >=, && ||, ?: and the
-- functions below. (Bitwise operators aren't used by these skins.)
local FUNCS = {
  abs = math.abs, sqrt = math.sqrt, sin = math.sin, cos = math.cos, tan = math.tan, asin = math.asin, acos = math.acos,
  atan = math.atan, atan2 = math.atan2, exp = math.exp, ln = math.log, log = math.log10, floor = math.floor,
  ceil = math.ceil, rad = math.rad, deg = math.deg,
  trunc = function(x) return x >= 0 and math.floor(x) or math.ceil(x) end,
  frac = function(x) return x - (x >= 0 and math.floor(x) or math.ceil(x)) end,
  sgn = function(x) return x > 0 and 1 or (x < 0 and -1 or 0) end,
  round = function(x, p) local m = 10 ^ (p or 0); return math.floor(x * m + 0.5) / m end,
  min = function(...) return math.min(...) end, max = function(...) return math.max(...) end,
  clamp = function(x, lo, hi) return math.max(lo, math.min(hi, x)) end,
}
local CONSTS = { pi = math.pi, e = math.exp(1) }

local function tokenize(s)
  local toks, i = {}, 1
  while i <= #s do
    local c = s:sub(i, i)
    if c:match('%s') then i = i + 1
    else
      local num = s:match('^%d*%.?%d+', i) or s:match('^%d+%.?', i)
      local name = s:match('^[%a_][%w_]*', i)
      local op = s:match('^%*%*', i) or s:match('^<>', i) or s:match('^<=', i) or s:match('^>=', i)
                 or s:match('^&&', i) or s:match('^||', i) or s:match('^[%+%-%*/%%%(%)%?:,=<>]', i)
      if num then toks[#toks + 1] = { 'num', tonumber(num) }; i = i + #num
      elseif name then toks[#toks + 1] = { 'name', name }; i = i + #name
      elseif op then toks[#toks + 1] = { 'op', op }; i = i + #op
      else error('unexpected "' .. c .. '"', 0) end
    end
  end
  return toks
end

-- Precedence climbing over the tokens; returns the value
local function evaluate(s)
  local toks, pos = tokenize(s), 1
  local function peek() return toks[pos] end
  local function take(op)
    local t = toks[pos]
    if t and t[1] == 'op' and t[2] == op then pos = pos + 1; return true end
    return false
  end
  local function expect(op) if not take(op) then error('expected "' .. op .. '"', 0) end end
  local BIN = {
    { '||' }, { '&&' }, { '=', '<>' }, { '<', '>', '<=', '>=' }, { '+', '-' }, { '*', '/', '%' }, { '**' } }
  local cond
  local function unary()
    if take('-') then return -unary() end
    if take('+') then return unary() end
    local t = peek()
    if not t then error('formula ends early', 0) end
    pos = pos + 1
    if t[1] == 'num' then return t[2] end
    if t[1] == 'op' and t[2] == '(' then local v = cond(); expect(')'); return v end
    if t[1] == 'name' then
      local n = lower(t[2])
      if CONSTS[n] then return CONSTS[n] end
      local f = FUNCS[n]
      if not f then error('unknown name "' .. t[2] .. '"', 0) end
      expect('(')
      local args = { cond() }
      while take(',') do args[#args + 1] = cond() end
      expect(')')
      return f(unpack(args))
    end
    error('unexpected "' .. tostring(t[2]) .. '"', 0)
  end
  local function level(n)
    if n > #BIN then return unary() end
    local v = level(n + 1)
    while true do
      local t, matched = peek(), nil
      if t and t[1] == 'op' then for _, op in ipairs(BIN[n]) do if t[2] == op then matched = op end end end
      if not matched then return v end
      pos = pos + 1
      local r = (matched == '**') and level(n) or level(n + 1)   -- power binds to the right
      if matched == '+' then v = v + r elseif matched == '-' then v = v - r
      elseif matched == '*' then v = v * r elseif matched == '/' then v = (r == 0) and 0 or v / r
      elseif matched == '%' then v = (r == 0) and 0 or math.fmod(v, r) elseif matched == '**' then v = v ^ r
      elseif matched == '=' then v = (v == r) and 1 or 0 elseif matched == '<>' then v = (v ~= r) and 1 or 0
      elseif matched == '<' then v = (v < r) and 1 or 0 elseif matched == '>' then v = (v > r) and 1 or 0
      elseif matched == '<=' then v = (v <= r) and 1 or 0 elseif matched == '>=' then v = (v >= r) and 1 or 0
      elseif matched == '&&' then v = (v ~= 0 and r ~= 0) and 1 or 0
      elseif matched == '||' then v = (v ~= 0 or r ~= 0) and 1 or 0 end
    end
  end
  function cond()
    local c = level(1)
    if take('?') then
      local a = cond(); expect(':'); local b = cond()
      return c ~= 0 and a or b
    end
    return c
  end
  local v = cond()
  if pos <= #toks then error('unexpected "' .. tostring(toks[pos][2]) .. '"', 0) end
  return v
end
M.evaluate = evaluate

-- ---------- bangs ----------
-- Every bang Rainmeter 4.5 knows (lower case), with the fewest arguments it needs
local BANGS = {}
for name, n in pairs({
  setvariable = 2, setvariablegroup = 3, setoption = 3, setoptiongroup = 3, writekeyvalue = 3, update = 0, redraw = 0,
  refresh = 0, refreshapp = 0, refreshgroup = 1, updatemeter = 1, updatemetergroup = 1, updatemeasure = 1,
  updatemeasuregroup = 1, showmeter = 1, hidemeter = 1, togglemeter = 1, showmetergroup = 1, hidemetergroup = 1,
  togglemetergroup = 1, movemeter = 3, enablemeasure = 1, disablemeasure = 1, togglemeasure = 1,
  enablemeasuregroup = 1, disablemeasuregroup = 1, togglemeasuregroup = 1, pausemeasure = 1, unpausemeasure = 1,
  togglepausemeasure = 1, pausemeasuregroup = 1, unpausemeasuregroup = 1, togglepausemeasuregroup = 1,
  commandmeasure = 2, pluginbang = 1, show = 0, hide = 0, toggle = 0, showfade = 0, hidefade = 0, togglefade = 0,
  showgroup = 1, hidegroup = 1, togglegroup = 1, showfadegroup = 1, hidefadegroup = 1, togglefadegroup = 1,
  fadeduration = 1, fadedurationgroup = 2, move = 2, zpos = 1, zposgroup = 2, settransparency = 1,
  settransparencygroup = 2, draggable = 1, draggablegroup = 2, keeponscreen = 1, keeponscreengroup = 2,
  clickthrough = 1, clickthroughgroup = 2, snapedges = 1, snapedgesgroup = 2, autoselectscreen = 1,
  autoselectscreengroup = 2, setwindowposition = 2, setanchor = 2, activateconfig = 1, deactivateconfig = 0,
  toggleconfig = 2, deactivateconfiggroup = 1, loadlayout = 1, skinmenu = 0, traymenu = 0, about = 0, manage = 0,
  log = 1, quit = 0, delay = 1, setclip = 1, clearclip = 0, setwallpaper = 1, execute = 1, editskin = 0,
  skincustommenu = 0, focus = 0, enablemouseaction = 2, disablemouseaction = 2, togglemouseaction = 2,
  clearmouseaction = 2, enablemouseactiongroup = 3, disablemouseactiongroup = 3, togglemouseactiongroup = 3,
  clearmouseactiongroup = 3, resetstats = 0, updatemetergroups = 1, showblur = 0, hideblur = 0, addblur = 1,
  removeblur = 1, setaccent = 1,
}) do BANGS[name] = n end

-- Splits "[!A x "y z"][!B]" (or a bare "!A x") into bangs, each a list of words; a quoted word keeps its spaces
local function splitBangs(text)
  local out = {}
  local function words(s)
    local w, i = {}, 1
    while i <= #s do
      local c = s:sub(i, i)
      if c:match('%s') then i = i + 1
      elseif s:sub(i, i + 2) == '"""' then
        local j = s:find('"""', i + 3, true) or (#s + 1)
        w[#w + 1] = s:sub(i + 3, j - 1); i = j + 3
      elseif c == '"' then
        local j = s:find('"', i + 1, true) or (#s + 1)
        w[#w + 1] = s:sub(i + 1, j - 1); i = j + 1
      else
        local j = s:find('%s', i) or (#s + 1)
        w[#w + 1] = s:sub(i, j - 1); i = j
      end
    end
    return w
  end
  text = trim(text)
  if text:sub(1, 1) ~= '[' then
    if text ~= '' then out[1] = words(text) end
    return out
  end
  -- Brackets nest when a bang's argument holds more bangs ([!Delay 100][!CommandMeasure X "[!A]"] style)
  local depth, start, quoted = 0, nil, false
  for i = 1, #text do
    local c = text:sub(i, i)
    if c == '"' then quoted = not quoted
    elseif not quoted and c == '[' then depth = depth + 1; if depth == 1 then start = i + 1 end
    elseif not quoted and c == ']' then
      depth = depth - 1
      if depth == 0 and start then out[#out + 1] = words(text:sub(start, i - 1)); start = nil end
    end
  end
  return out
end
M.splitBangs = splitBangs

-- ---------- reading a skin ----------
local SECTION = '^%s*%[([^%]]+)%]%s*$'

-- Reads path (and its @Includes) into skin.sections (lower-case name -> { name, keys = { lower -> value } }) and
-- skin.order, and [Variables] into skin.vars in Rainmeter's order
local function readIni(skin, path, depth)
  local f = io.open(path, 'rb')
  if not f then skin:problem('cannot read ' .. path); return end
  local text = f:read('*a'); f:close()
  text = text:gsub('^\239\187\191', '')   -- UTF-8 byte order mark
  local cur
  for line in (text .. '\n'):gmatch('([^\n]*)\n') do
    line = trim(line:gsub('\r$', ''))
    if line ~= '' and line:sub(1, 1) ~= ';' then
      local name = line:match(SECTION)
      if name then
        local key = lower(trim(name))
        cur = skin.sections[key]
        if not cur then
          cur = { name = trim(name), keys = {}, order = {} }
          skin.sections[key] = cur
          skin.order[#skin.order + 1] = cur
        end
      elseif cur then
        local k, v = line:match('^([^=]-)%s*=%s*(.*)$')
        if k then
          k = trim(k)
          if lower(cur.name) == 'variables' and lower(k):match('^@include%d*$') then
            local inc = unixPath(skin:replaceVariables(v:gsub('^"(.*)"$', '%1')))
            if depth < 5 then readIni(skin, inc, depth + 1) end
            cur = skin.sections['variables']
          elseif lower(cur.name) == 'variables' then
            skin:setVariable(k, skin:replaceVariables(v))
          else
            if cur.keys[lower(k)] == nil then cur.order[#cur.order + 1] = k end
            cur.keys[lower(k)] = v
          end
        end
      end
    end
  end
end

local Skin = {}
Skin.__index = Skin

function Skin:problem(msg)
  local where = self.config .. (self.context and (' (' .. self.context .. ')') or '')
  self.problems[#self.problems + 1] = where .. ': ' .. msg
end

function Skin:setVariable(name, value) self.vars[lower(name)] = { name = name, value = tostring(value) } end
function Skin:variable(name)
  local v = self.vars[lower(name)]
  if v then return v.value end
  return self.builtin[lower(name)]
end

-- #Name# -> its value, in one pass as Rainmeter does (a [Variables] value was already replaced when it was read);
-- #*Name*# -> the text #Name#
function Skin:replaceVariables(s)
  s = s:gsub('#([%w_@%.]+)#', function(n) return self:variable(n) end)
  return (s:gsub('#%*([%w_@%.]+)%*#', '#%1#'))
end

-- [Meter:X] / [Meter:W] ... and [Measure] in a formula
function Skin:replaceSections(s, seen)
  return (s:gsub('%[([%w_]+):([XYWH])%]', function(name, what)
    local m = self.meters[lower(name)]
    if not m then return nil end
    return tostring(m['Get' .. what](m, seen))
  end):gsub('%[([%w_]+)%]', function(name)
    local m = self.measures[lower(name)]
    if m then return tostring(m:GetValue()) end
  end))
end

-- An option as the skin sees it: its own value, else its MeterStyles' (the last style listed wins)
function Skin:option(section, key)
  local sec = self.sections[lower(section)]
  if not sec then return nil end
  local o = self.overrides[lower(section)]
  if o and o[lower(key)] ~= nil then return o[lower(key)] end
  if sec.keys[lower(key)] ~= nil then return sec.keys[lower(key)] end
  local styles = (o and o.meterstyle) or sec.keys.meterstyle
  if styles then
    local list = {}
    for st in styles:gmatch('[^|]+') do list[#list + 1] = trim(st) end
    for i = #list, 1, -1 do
      local st = self.sections[lower(self:replaceVariables(list[i]))]
      if st and st.keys[lower(key)] ~= nil then return st.keys[lower(key)] end
    end
  end
  return nil
end

function Skin:formula(text, what, seen)
  local s = self:replaceSections(self:replaceVariables(text), seen)
  local ok, v = pcall(evaluate, s)
  if ok and v == v and v ~= math.huge and v ~= -math.huge then return v end
  self:problem(what .. ': the formula ' .. text .. ' does not evaluate (' .. (ok and 'not a number' or v) .. ')')
  return 0
end

-- A number option: a formula in parentheses, or a number with Rainmeter's r/R (relative) suffix, which is ignored
function Skin:number(section, key, default, seen)
  local v = self:option(section, key)
  if v == nil or trim(v) == '' then return default end
  v = trim(self:replaceVariables(v)):gsub('[rR]$', '')
  if v:sub(1, 1) == '(' then return self:formula(v, section .. ' ' .. key, seen) end
  return tonumber(v) or default
end

-- ---------- meters and measures ----------
local Meter = {}
Meter.__index = Meter

local function newMeter(skin, name) return setmetatable({ skin = skin, name = name }, Meter) end
function Meter:GetName() return self.name end
function Meter:GetOption(key, default)
  local v = self.skin:option(self.name, key)
  if v == nil then return default or '' end
  return self.skin:replaceVariables(v)
end
function Meter:GetNumberOption(key, default) return tonumber(self:GetOption(key, '')) or default or 0 end
-- Positions are what X and Y evaluate to; a relative position (0R) counts from 0, which is close enough here
function Meter:GetX(seen) return self.skin:number(self.name, 'X', 0, seen) end
function Meter:GetY(seen) return self.skin:number(self.name, 'Y', 0, seen) end
-- Sizes: W/H if set, else a String meter's text measured at about 0.55 em per character
function Meter:size(key, seen)
  seen = seen or {}
  local id = self.name .. ':' .. key
  if seen[id] then self.skin:problem(id .. ' depends on itself'); return 0 end
  seen[id] = true
  local v = self.skin:number(self.name, key, nil, seen)
  seen[id] = nil
  if v then return v end
  if lower(self:GetOption('Meter')) ~= 'string' then return 0 end
  local size = tonumber(self.skin:replaceVariables(self:GetOption('FontSize', '10'))) or 10
  local text = self:GetOption('Text', '')
  local pad = {}
  for n in self:GetOption('Padding', '0,0,0,0'):gmatch('[^,]+') do pad[#pad + 1] = tonumber(n) or 0 end
  if key == 'W' then return math.floor(#text * size * 0.55) + (pad[1] or 0) + (pad[3] or 0) end
  return math.floor(size * 1.35) + (pad[2] or 0) + (pad[4] or 0)
end
function Meter:GetW(seen) return self:size('W', seen) end
function Meter:GetH(seen) return self:size('H', seen) end
function Meter:Show() self.skin:bang({ '!ShowMeter', self.name }) end
function Meter:Hide() self.skin:bang({ '!HideMeter', self.name }) end
function Meter:SetX(v) self.skin:bang({ '!SetOption', self.name, 'X', tostring(v) }) end
function Meter:SetY(v) self.skin:bang({ '!SetOption', self.name, 'Y', tostring(v) }) end
function Meter:SetW(v) self.skin:bang({ '!SetOption', self.name, 'W', tostring(v) }) end
function Meter:SetH(v) self.skin:bang({ '!SetOption', self.name, 'H', tostring(v) }) end
function Meter:hidden() return (tonumber(self:GetOption('Hidden', '0')) or 0) ~= 0 end

local Measure = {}
Measure.__index = Measure
local function newMeasure(skin, name) return setmetatable({ skin = skin, name = name }, Measure) end
Measure.GetName, Measure.GetOption, Measure.GetNumberOption = Meter.GetName, Meter.GetOption, Meter.GetNumberOption
-- Measures report nothing here (no plugins run); a Script measure reports what its Update returned
function Measure:GetValue() return tonumber(self.value) or 0 end
function Measure:GetStringValue() return self.value ~= nil and tostring(self.value) or '' end

-- ---------- scripts ----------
-- Runs a script in its own environment, as Rainmeter does, with SKIN, SELF and the sandboxed file functions
function Skin:loadScript(measure, path)
  local chunk, err = loadfile(path)
  if not chunk then self:problem(err); return end
  local sandbox = self.sandbox
  local env = setmetatable({}, { __index = _G })
  env._G = env
  env.SKIN, env.SELF = self.api, measure
  env.print = function() end
  env.os = setmetatable({
    getenv = function(n)
      n = n:upper()
      if n == 'LOCALAPPDATA' then return sandbox .. '/Local' end
      if n == 'USERPROFILE' then return sandbox .. '/Home' end
      if n == 'APPDATA' then return sandbox .. '/Roaming' end
      return nil
    end,
    remove = function(p) return os.remove(unixPath(p)) end,
    rename = function(a, b) return os.rename(unixPath(a), unixPath(b)) end,
    execute = function() self:problem('os.execute is not allowed in a skin script'); return 1 end,
  }, { __index = os })
  env.io = setmetatable({
    open = function(p, mode) return io.open(unixPath(p), mode) end,
    lines = function(p) return io.lines(unixPath(p)) end,
    popen = function() self:problem('io.popen is not allowed in a skin script') end,
  }, { __index = io })
  setfenv(chunk, env)
  local script = { measure = measure, env = env, path = path }
  self.scripts[lower(measure.name)] = script
  self:call(script, function() chunk() end, 'loading ' .. path)
  return script
end

function Skin:call(script, fn, what)
  local before = self.context
  self.context = what
  local ok, err = pcall(fn)
  if not ok then
    -- "…/Skins/Loopscape/Settings/settings.lua:12: …" -> "rainmeter/Loopscape/Settings/settings.lua:12: …"
    local msg = tostring(err):gsub('[^%s]*/Skins/Loopscape/', 'rainmeter/Loopscape/')
    self:problem('Lua error: ' .. msg)
  end
  self.context = before
  return ok
end

-- Calls a global function of the script if it has one (Initialize, Update)
function Skin:callFunction(script, name)
  local f = rawget(script.env, name)
  if type(f) ~= 'function' then return end
  self:call(script, function() script.measure.value = f() end, script.measure.name .. ' ' .. name .. '()')
end

-- ---------- carrying out bangs ----------
local function sameConfig(skin, config) return config == nil or config == '' or lower(config) == lower(skin.config) end

function Skin:setOverride(section, key, value)
  local s = lower(section)
  self.overrides[s] = self.overrides[s] or {}
  self.overrides[s][lower(key)] = value
end

function Skin:meterGroup(group)
  local list = {}
  for _, sec in ipairs(self.order) do
    local m = self.meters[lower(sec.name)]
    if m then
      for g in (self:option(sec.name, 'Group') or ''):gmatch('[^|]+') do
        if lower(trim(self:replaceVariables(g))) == lower(group) then list[#list + 1] = m end
      end
    end
  end
  return list
end

-- words: { '!Name', arg, ... }. Records the bang in skin.bangs and applies the ones that change state.
function Skin:bang(words)
  local name = words[1]
  if type(name) ~= 'string' then self:problem('a bang with no name'); return end
  if name:sub(1, 1) == '"' or not name:match('^!') then
    self.bangs[#self.bangs + 1] = words   -- runs a program ("#CONFIGEDITOR#" ...): not run here
    return
  end
  local key = lower(name:sub(2))
  local need = BANGS[key]
  if not need then self:problem('unknown bang ' .. name); return end
  for i = 2, #words do
    if words[i] == nil then self:problem(name .. ': argument ' .. (i - 1) .. ' is nil'); return end
  end
  if #words - 1 < need then
    self:problem(name .. ' needs ' .. need .. ' argument(s), got ' .. (#words - 1) .. ': ' .. table.concat(words, ' '))
    return
  end
  self.bangs[#self.bangs + 1] = words
  local a = words
  if key == 'setvariable' then
    if sameConfig(self, a[4]) then self:setVariable(a[2], a[3]) else self:sent(a) end
  elseif key == 'setoption' then
    if sameConfig(self, a[5]) then
      if not self.sections[lower(a[2])] then self:problem('!SetOption on unknown section ' .. a[2]) end
      self:setOverride(a[2], a[3], a[4])
    else self:sent(a) end
  elseif key == 'setoptiongroup' then
    for _, m in ipairs(self:meterGroup(a[2])) do self:setOverride(m.name, a[3], a[4]) end
  elseif key == 'showmeter' or key == 'hidemeter' or key == 'togglemeter' then
    if sameConfig(self, a[3]) then
      local m = self.meters[lower(a[2])]
      if not m and a[2] ~= '*' then self:problem(name .. ' on unknown meter ' .. a[2])
      elseif m then
        local hide = key == 'hidemeter' or (key == 'togglemeter' and not m:hidden())
        self:setOverride(m.name, 'Hidden', hide and '1' or '0')
      end
    end
  elseif key == 'showmetergroup' or key == 'hidemetergroup' or key == 'togglemetergroup' then
    if sameConfig(self, a[3]) then
      for _, m in ipairs(self:meterGroup(a[2])) do
        local hide = key == 'hidemetergroup' or (key == 'togglemetergroup' and not m:hidden())
        self:setOverride(m.name, 'Hidden', hide and '1' or '0')
      end
    end
  elseif key == 'updatemeter' or key == 'updatemeasure' then
    local t = key == 'updatemeter' and self.meters or self.measures
    if sameConfig(self, a[3]) and a[2] ~= '*' and not t[lower(a[2])] then self:problem(name .. ' on unknown section ' .. a[2]) end
  elseif key == 'commandmeasure' then
    if sameConfig(self, a[4]) then
      local script = self.scripts[lower(a[2])]
      if script then self:runCode(script, a[3])
      elseif not self.measures[lower(a[2])] then self:problem('!CommandMeasure on unknown measure ' .. a[2]) end
    else self:sent(a) end
  elseif key == 'writekeyvalue' then
    -- Not written: the skins' .ini files stay as they are. Recorded in skin.written for the tests.
    self.written[#self.written + 1] = { section = a[2], key = a[3], value = a[4], file = a[5] }
  end
end

-- A bang meant for another skin
function Skin:sent(words) self.outbox[#self.outbox + 1] = words end

-- Lua code from !CommandMeasure, with the mouse at (10, 10)
function Skin:runCode(script, code)
  code = code:gsub('%$Mouse([XY]):?%%?%$', '10')
  local chunk, err = loadstring(code, '=' .. script.measure.name .. ' command')
  if not chunk then self:problem('!CommandMeasure ' .. script.measure.name .. ' "' .. code .. '": ' .. err); return end
  setfenv(chunk, script.env)
  self.commands = (self.commands or 0) + 1
  self:call(script, chunk, script.measure.name .. ' "' .. code .. '"')
end

-- An action option's text: variables replaced, then each bang carried out
function Skin:action(text, what)
  local before = self.context
  self.context = what
  for _, words in ipairs(splitBangs(self:replaceVariables(text))) do self:bang(words) end
  self.context = before
end

-- ---------- making a skin ----------
-- opts: sandbox (folder), skins (the copied Skins folder), path (its .ini), config ("Loopscape\Clock")
function M.load(opts)
  local skin = setmetatable({
    sandbox = opts.sandbox, config = opts.config, path = opts.path, sections = {}, order = {}, vars = {},
    overrides = {}, meters = {}, measures = {}, scripts = {}, problems = {}, bangs = {}, outbox = {}, written = {},
  }, Skin)
  local folder = opts.path:match('^(.*/)')
  skin.builtin = {
    ['@'] = opts.skins .. 'Loopscape/@Resources/', currentpath = folder, skinspath = opts.skins,
    rootconfigpath = opts.skins .. 'Loopscape/', currentconfig = opts.config, rootconfig = 'Loopscape',
    currentfile = opts.path:match('([^/]+)$'), settingspath = opts.sandbox .. '/Roaming/Rainmeter/',
    programpath = 'C:/Program Files/Rainmeter/', programdrive = 'C:', addonspath = opts.skins .. '@Vault/',
    pluginspath = opts.sandbox .. '/Roaming/Rainmeter/Plugins/', configeditor = 'notepad.exe', crlf = '\n',
    currentconfigx = '100', currentconfigy = '100', currentconfigwidth = '300', currentconfigheight = '200',
  }
  for _, area in ipairs({ 'screenarea', 'workarea', 'vscreenarea', 'vworkarea', 'pscreenarea', 'pworkarea' }) do
    local h = area:find('work') and 1040 or 1080
    for _, suffix in ipairs({ '', '@1' }) do
      skin.builtin[area .. 'x' .. suffix] = '0'; skin.builtin[area .. 'y' .. suffix] = '0'
      skin.builtin[area .. 'width' .. suffix] = '1920'; skin.builtin[area .. 'height' .. suffix] = tostring(h)
    end
  end
  skin.sections['variables'] = { name = 'Variables', keys = {}, order = {} }
  skin.order[1] = skin.sections['variables']
  readIni(skin, opts.path, 0)

  -- The SKIN object scripts see
  local api = {}
  skin.api = api
  function api:GetVariable(name, default)
    if type(name) ~= 'string' then skin:problem('SKIN:GetVariable with a ' .. type(name) .. ' name'); return default end
    local v = skin:variable(name)
    if v == nil then return default end
    return v
  end
  function api:ReplaceVariables(s) return skin:replaceVariables(tostring(s)) end
  function api:ParseFormula(s)
    if type(s) ~= 'string' then return tonumber(s) end
    return skin:formula(s, 'SKIN:ParseFormula')
  end
  function api:MakePathAbsolute(p) return skin.builtin.currentpath .. p end
  function api:GetMeter(name) return skin.meters[lower(tostring(name))] end
  function api:GetMeasure(name) return skin.measures[lower(tostring(name))] end
  function api:GetX() return tonumber(skin.builtin.currentconfigx) end
  function api:GetY() return tonumber(skin.builtin.currentconfigy) end
  function api:GetW() return tonumber(skin.builtin.currentconfigwidth) end
  function api:GetH() return tonumber(skin.builtin.currentconfigheight) end
  function api:Bang(...)
    local n = select('#', ...)
    local args = { ... }
    for i = 1, n do
      if args[i] == nil then skin:problem('SKIN:Bang argument ' .. i .. ' is nil'); return end
      args[i] = tostring(args[i])
    end
    if n == 1 then
      for _, words in ipairs(splitBangs(args[1])) do skin:bang(words) end
    else
      skin:bang(args)
    end
  end

  for _, sec in ipairs(skin.order) do
    local k = lower(sec.name)
    if sec.keys.meter then skin.meters[k] = newMeter(skin, sec.name)
    elseif sec.keys.measure then skin.measures[k] = newMeasure(skin, sec.name) end
  end
  return skin
end

-- Loads the skin's Script measures and runs Initialize, then Update once, as Rainmeter does on load
function Skin:start()
  local list = {}
  for _, sec in ipairs(self.order) do
    if sec.keys.measure and lower(trim(sec.keys.measure)) == 'script' then
      local file = unixPath(self:replaceVariables(sec.keys.scriptfile or ''))
      local script = self:loadScript(self.measures[lower(sec.name)], file)
      if script then list[#list + 1] = script end
    end
  end
  for _, s in ipairs(list) do self:callFunction(s, 'Initialize') end
  local onRefresh = self:option('Rainmeter', 'OnRefreshAction')
  if onRefresh then self:action(onRefresh, 'OnRefreshAction') end
  self:tick()
  return self
end

function Skin:tick()
  for _, s in pairs(self.scripts) do self:callFunction(s, 'Update') end
end

-- Every action option in the skin, in file order: { section, key, text }
local ACTION_KEY = 'action%d*$'
function Skin:actions()
  local out = {}
  for _, sec in ipairs(self.order) do
    if lower(sec.name) ~= 'variables' then
      for _, k in ipairs(sec.order) do
        if lower(k):match(ACTION_KEY) then out[#out + 1] = { sec.name, k, sec.keys[lower(k)] } end
      end
    end
  end
  return out
end

-- Every meter's position and size, evaluated once: catches formulas that don't evaluate
function Skin:measureAll()
  for _, sec in ipairs(self.order) do
    local m = self.meters[lower(sec.name)]
    if m then m:GetX(); m:GetY(); m:GetW(); m:GetH() end
  end
end

M.Skin = Skin
return M
