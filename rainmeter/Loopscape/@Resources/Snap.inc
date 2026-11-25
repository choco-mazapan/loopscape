; Shared snapping for Loopscape widgets (include with @Include=#@#Snap.inc at the top of [Variables]).
; After you drag a widget and let go, it moves to the nearest free target: one of 8 screen spots (corners,
; top/bottom centre, left/right middle; bottom spots leave room for the auto-hiding taskbar) or a stack spot
; directly above/below another widget. Stacked widgets follow the one they sit on.
; On/off (SnapEnabled) lives in Settings.inc. SnapAttach is written into a widget's own [Variables]
; automatically when it is stacked.
[Variables]
SnapMargin=28
; Gap under the bottom spots. Default only: the Settings panel ("Bottom spots") sets it in Settings.inc
SnapBottomMargin=76
; Space between stacked widgets
SnapGap=12
; Same, when the panel tint is off (PanelAlpha=0): the panels' padding is invisible then, so they overlap by this
; much to bring the text lines together
SnapGapClear=-28
; Widgets that know about each other (config names, separated by |)
SnapPeers=Loopscape\Clock|Loopscape\WiFi|Loopscape\NowShowing|Loopscape\SunMoon|Loopscape\Rain|Loopscape\System|Loopscape\Calendar
SnapAttach=
; Size the widget had last time, written automatically (snap.lua: used while it loads)
SnapSize=
; Which screen column the widget is in (0 left, 1 centre, 2 right), set by snap.lua: one-line views line their
; text up with it, so a stack reads as one block in every column
ColumnAlign=0
; Run by each widget as it closes ([Rainmeter] OnCloseAction): the others check who is still there (snap.lua PeerGone)
SnapOnClose=[!CommandMeasure MeasureSnap "PeerGone()" "Loopscape\Clock"][!CommandMeasure MeasureSnap "PeerGone()" "Loopscape\WiFi"][!CommandMeasure MeasureSnap "PeerGone()" "Loopscape\NowShowing"][!CommandMeasure MeasureSnap "PeerGone()" "Loopscape\SunMoon"][!CommandMeasure MeasureSnap "PeerGone()" "Loopscape\Rain"][!CommandMeasure MeasureSnap "PeerGone()" "Loopscape\System"][!CommandMeasure MeasureSnap "PeerGone()" "Loopscape\Calendar"]

[MeasureSnap]
Measure=Script
ScriptFile=#@#snap.lua
UpdateDivider=1