## What changes

<!-- What a user of the UI or the JSON API sees before and after this change. Link the issue it fixes, if any. -->

## How

<!-- A short note on the approach, if it isn't obvious from the diff. -->

## Checklist

- [ ] `dotnet build -c Release` and `dotnet test -c Release --no-build` pass
- [ ] No web or ASP.NET Core dependency added to `src/FalconLoad.Core`
- [ ] Engine changes have a test in `tests/FalconLoad.Core.Tests` that runs against `FakeHandler`, not the network
- [ ] The UI change was tried in a browser against a real run
- [ ] `README.md` updated if a feature, dynamic value or API endpoint was added or changed
