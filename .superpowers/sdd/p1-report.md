# Phase 1 report

Status: DONE (orchestrator). Not yet compiled — waiting on P2/P4 so obj/bin is free.

## Implemented
- BrowseRoutes.Home = "browse" + IsHome(); prefix Is() unchanged
- NavRouteNormalizer: empty search → browse; legacy recents → recents. Called from Go, RestorePinnedWorkspace, RestoreSessionNav
- BrowseDirectoryPage extracted from SearchPage.EmptyLanding; ScrollKey = "browse"
- SearchPage takes Route snapshot; empty-query/masthead/EmptyLanding deleted; ScrollKey = "search:" + facet
- ContentHost: pages get Route values; browse-home arm before search; SearchPage(r)
- PageNavMotion.SlotKey: uniform tab/name/arg (search special case gone)
- ShellMastheadBand: IsHome family + route-derived title/caption, no publisher
- DrillTrail Browse crumb → BrowseRoutes.Home
- ShellNav Dest browse arm; HistoryPage browse kind; HomePage/BrowsePageHost retargeted
- Tests: NavRouteNormalizerTests; slot tests; DrillTrail/ShellNavDest/WaveeNavProbe lists
- BrowseDirectoryStore rationale rewritten for cold-start/eviction

## Files
New: NavRouteNormalizer.cs, BrowseDirectoryPage.cs, NavRouteNormalizerTests.cs
Edited: BrowseRoutes, SearchPage, ContentHost, PageNavMotion, WaveeShell, ShellMastheadBand, DrillTrail, ShellNav, HistoryPage, HomePage, BrowsePageHost, BrowseDirectory, BrowseDirectoryStore, WaveeNavProbe, tests, Wavee.Tests.csproj
