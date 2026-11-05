# Security policy

## Reporting a vulnerability

Please report security problems privately, not in a public issue. Use GitHub's
[private vulnerability reporting](https://github.com/Moonwalker1307/falcon-load/security/advisories/new) for this
repository, with the steps to reproduce and the version or commit you tested.

You'll get an answer within a week. Once a fix is out, the advisory is published with credit to you, unless you'd
rather stay anonymous.

## What counts

falcon-load is meant to run on your own machine or a private network, and it sends requests to any URL it is given.
Reports are most useful about:

- A way for someone who isn't the operator to start a test, read results or reach the API, for example through agent
  mode or a default that exposes the app.
- Secrets of an environment, OAuth2 tokens or credential headers showing up where they shouldn't: the UI, the API, an
  export, a report, an error sample or a log.
- Code execution or a crash from a crafted file: an imported test, Postman collection, OpenAPI spec, data file or proto.
- A problem in the GitHub Action or the Azure Pipelines template, such as an input that runs as shell code.

That falcon-load can send heavy traffic to a URL its operator chooses is its purpose, not a vulnerability; see
[Responsible use](README.md#responsible-use).
