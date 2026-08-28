# Nav structural fix — progress ledger

- P0: complete (commits d8a83fd1d engine, 26c56a4c8 wavee). Working tree was dirty feature work; committed as-is on main.
- P1: in progress (orchestrator, app)
- P2: dispatched (engine exit-freeze)
- P3: blocked on P1
- P4: dispatched (scroll restore goal)
- P5: blocked on P2 (both touch Reconciler.cs BeginKeepAliveExit)
- P6: blocked on P1
