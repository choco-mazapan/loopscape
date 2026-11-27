; Widget size, shared by every Loopscape widget and the view menu. Each includes this right after Settings.inc and
; multiplies its sizes, gaps and font sizes by #Scale# (ClockSize=(66*#Scale#) and so on), so a widget keeps its
; proportions and its text pills keep fitting the letters at any size.
; WidgetScale (Settings > Widgets > Widget size, stored in Settings.inc): 0 = automatic, or a factor such as 1.5.
; Automatic: the screen height over 1080, in steps of 0.25 from 1 to 3. 1080p screens and smaller stay at 1;
; 1440p gives 1.25, 1600p 1.5, 2160p (4K) 2.
; Rainmeter fills in variables when it reads them, so Scale must come after Settings.inc, and the sizes after Scale.
; A change needs a refresh: the Settings panel refreshes the widgets that are running.
[Variables]
Scale=(#WidgetScale#>0 ? Clamp(#WidgetScale#,0.5,4) : Clamp(Round(#SCREENAREAHEIGHT#*4/1080)/4,1,3))
