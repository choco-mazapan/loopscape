#!/usr/bin/env python3
"""Checks the #Variables# in the Loopscape Rainmeter skins.

Rainmeter reads a skin's [Variables] section top to bottom, @Include files at the line that includes them, and
replaces each #Name# in a value with what that variable holds at that moment. A variable used before its definition
stays as the literal text "#Name#", so a formula built from it quietly fails (Now Showing's buttons once drew at the
top of the widget this way). This script reports:

  - a [Variables] value that uses a variable defined only further down, or in a later @Include
  - a #Name# anywhere in the skin that nothing defines: not [Variables], not an @Include, not a Rainmeter built-in,
    and not a variable the skin's Lua scripts set with !SetVariable

Usage: python3 rainmeter/check-skin-variables.py [skins folder]   (default: rainmeter/Loopscape next to this script)
Exits with 1 and prints one line per problem (as a GitHub Actions error annotation) if it finds any.
"""
import os
import re
import sys

# Variables Rainmeter defines itself. The screen and work area ones also come with a monitor number (@1, @2, ...).
BUILTIN = {
    '@', 'CURRENTSECTION', 'CURRENTFILE', 'CURRENTPATH', 'CURRENTCONFIG', 'ROOTCONFIG', 'ROOTCONFIGPATH',
    'SKINSPATH', 'SETTINGSPATH', 'PROGRAMPATH', 'PROGRAMDRIVE', 'ADDONSPATH', 'PLUGINSPATH', 'CONFIGEDITOR',
    'CURRENTCONFIGX', 'CURRENTCONFIGY', 'CURRENTCONFIGWIDTH', 'CURRENTCONFIGHEIGHT', 'CRLF',
}
AREA = re.compile(r'^P?(SCREENAREA|WORKAREA|VSCREENAREA|VWORKAREA)(X|Y|WIDTH|HEIGHT)(@\d+)?$')

# #Name#, but not the escaped form #*Name*#, which Rainmeter writes out as the literal text #Name#
REF = re.compile(r'#(?!\*)([A-Za-z0-9_@.]+)#')
SECTION = re.compile(r'^\s*\[([^\]]+)\]\s*$')
INCLUDE_KEY = re.compile(r'^@include\d*$', re.I)
# !SetVariable with a literal name, optionally followed by '..' and more (a name built at runtime, such as 'RH' .. i)
LUA_SET = re.compile(r"""['"]!SetVariable['"]\s*,\s*['"]([A-Za-z0-9_]+)['"]\s*(\.\.)?""")


def builtin(name):
    return name.upper() in BUILTIN or bool(AREA.match(name.upper()))


def read_lines(path):
    """Yields (section, key, value, line number) for each key=value line, skipping comments and blank lines."""
    section = None
    with open(path, encoding='utf-8-sig', errors='replace') as f:
        for n, raw in enumerate(f, 1):
            line = raw.strip()
            if not line or line.startswith(';'):
                continue
            m = SECTION.match(line)
            if m:
                section = m.group(1).strip().lower()
                continue
            if '=' in line and section is not None:
                key, value = line.split('=', 1)
                yield section, key.strip(), value.strip(), n


def resolve_include(value, skins, skin_dir):
    """The path of an @Include value. Only the forms these skins use: #@#..., #CURRENTPATH#... or a relative path."""
    v = value.strip('"')
    resources = os.path.join(skins, '@Resources')
    v = re.sub(r'#@#', lambda _: resources + os.sep, v, flags=re.I)
    v = re.sub(r'#CURRENTPATH#', lambda _: skin_dir + os.sep, v, flags=re.I)
    v = v.replace('\\', os.sep)
    if '#' in v:
        return None
    return os.path.normpath(v if os.path.isabs(v) else os.path.join(skin_dir, v))


class Skin:
    """One .ini file with its includes, read the way Rainmeter reads it."""

    def __init__(self, path, skins):
        self.path, self.skins = path, skins
        self.defined = {}      # lower-case name -> (file, line) of its first definition in [Variables]
        self.refs = []         # (name, file, line) for every #Name# in the skin
        self.forward = []      # (file, [(name, key, line)]) for [Variables] values that use a name not defined yet
        self.problems = []
        self.read(path, os.path.dirname(path), [])

    def rel(self, path):
        return os.path.relpath(path, os.path.dirname(self.skins)).replace(os.sep, '/')

    def read(self, path, skin_dir, stack):
        if path in stack:
            self.problems.append((path, 1, 'includes itself'))
            return
        later = {}             # forward references in this file's [Variables], checked once the whole skin is read
        for section, key, value, n in read_lines(path):
            names = REF.findall(value)
            for name in names:
                self.refs.append((name, path, n))
            if section != 'variables':
                continue
            if INCLUDE_KEY.match(key):
                inc = resolve_include(value, self.skins, skin_dir)
                if inc is None or not os.path.isfile(inc):
                    self.problems.append((path, n, '%s=%s: included file not found' % (key, value)))
                else:
                    self.read(inc, skin_dir, stack + [path])
                continue
            for name in names:
                if not builtin(name) and name.lower() not in self.defined:
                    later.setdefault(name.lower(), []).append((name, key, n))
            self.defined.setdefault(key.lower(), (path, n))
        self.forward += [(path, uses) for uses in later.values()]

    def check(self, lua_names, lua_prefixes):
        for path, uses in self.forward:
            for name, key, n in uses:
                if name.lower() in self.defined:
                    where, line = self.defined[name.lower()]
                    self.problems.append((path, n, '%s uses #%s# before it is defined (%s line %d), so it reads '
                                          'as the text "#%s#"' % (key, name, self.rel(where), line, name)))
        seen = set()
        for name, path, n in self.refs:
            low = name.lower()
            if (builtin(name) or low in self.defined or low in lua_names
                    or any(low.startswith(p) for p in lua_prefixes) or (low, path, n) in seen):
                continue
            seen.add((low, path, n))
            self.problems.append((path, n, '#%s# is not defined in [Variables] or set by a script' % name))


def lua_variables(skins):
    """Names (and name prefixes) that the skins' Lua scripts set with !SetVariable."""
    names, prefixes = set(), set()
    for root, _, files in os.walk(skins):
        for f in files:
            if f.endswith('.lua'):
                with open(os.path.join(root, f), encoding='utf-8', errors='replace') as h:
                    for m in LUA_SET.finditer(h.read()):
                        (prefixes if m.group(2) else names).add(m.group(1).lower())
    return names, prefixes


def main():
    here = os.path.dirname(os.path.abspath(__file__))
    skins = os.path.abspath(sys.argv[1] if len(sys.argv) > 1 else os.path.join(here, 'Loopscape'))
    lua_names, lua_prefixes = lua_variables(skins)
    problems, count = set(), 0
    for root, dirs, files in os.walk(skins):
        dirs[:] = [d for d in dirs if d != '@Resources']
        for f in sorted(files):
            if f.lower().endswith('.ini'):
                skin = Skin(os.path.join(root, f), skins)
                skin.check(lua_names, lua_prefixes)
                count += 1
                for path, n, msg in skin.problems:
                    problems.add((skin.rel(path), n, msg))
    for path, n, msg in sorted(problems):
        print('::error file=rainmeter/%s,line=%d::%s' % (path, n, msg))
    if problems:
        print('%d problem(s) in the skins\' variables' % len(problems))
        return 1
    print('Checked the variables in %d skin(s): every one is defined before it is used.' % count)
    return 0


if __name__ == '__main__':
    sys.exit(main())
