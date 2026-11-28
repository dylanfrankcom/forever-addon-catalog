# Contributing

Thanks for helping. This catalog, like [Forever Add-on Manager](https://github.com/dylanfrankcom/forever-addon-manager), is free and
open source and takes no donations. If you'd like to support someone, support the add-on authors.

## How it's made
The catalog is maintained by AI agents (Claude Code) under the maintainer's direction. Every change goes through the same process: an
issue, a branch, validation, and a pull request that CI checks before it's merged. That's why contributions come as **issues, not pull
requests**.

## Asking for an add-on
Use the [Add an add-on](https://github.com/dylanfrankcom/forever-addon-catalog/issues/new?template=addon.yml) form. Fill in what you
know: where it's published, which release lists WoW Forever's Interface in its `.toc`, its folders, licence, issue tracker and support
page. Before an entry is added, the release is downloaded and its `.toc` read, and every source and link is checked. Only add-ons whose
current release lists the Forever Interface are listed.

Some add-ons can't be listed, for example when their only source has turned off downloads by other apps. They get an issue labelled
`unavailable` that says what would make it possible (the [Add-on that can't be listed](https://github.com/dylanfrankcom/forever-addon-catalog/issues/new?template=unavailable.yml) form). Comments there are welcome when something changes.

If it's your own add-on, see [For add-on authors](README.md#for-add-on-authors) too: publishing GitHub releases gives players one-click installs and updates.

## Version reports from the app
CurseForge and Wago don't let the catalog check their files automatically, so for add-ons downloaded from them the catalog relies
on players. With the player's permission, Forever Add-on Manager can report the version it downloaded and the zip's SHA-256, or
suggest an add-on the catalog doesn't list, without a GitHub account. Reports go to a small relay, which checks and counts them and
keeps them until the catalog collects them. The relay never writes to GitHub.

**Privacy:**
- **What's sent:** for a version report, the add-on's catalog ID, the source (CurseForge or Wago), the game edition, the version
  from the add-on's `.toc` file and the SHA-256 of the downloaded zip. For a suggestion, the source and project ID, the edition, the
  add-on's name and folder names, and its `.toc` title, version, interface numbers and author. The app asks before sending anything
  and shows exactly what it will send.
- **Never sent or kept:** files (the zip never leaves the player's PC), file paths, account names, machine details or anything else
  about the player.
- **Addresses:** the sender's IP address is never stored. The relay uses it in two ways only:
  - hashed, as the key of an in-memory rate limiter that limits how often each address can send, never written down;
  - to count how many different networks sent the same message. With that message only, the relay keeps a one-way value made
    from the network part of the address (the first three numbers of an IPv4 address, the first half of an IPv6 one), the
    message and a secret key only the relay holds. It can't be turned back into an address or matched with anything else, it's
    different for every message, so one person's messages can't be linked, and it's deleted with the message. The catalog gets
    only the number of networks, never the values.

  Request logging is off.
- **How long:** the relay deletes a message 7 days after the catalog collects it, and after 90 days if it's never collected. The
  catalog keeps only the checked fields above, without any address, until the version is confirmed, the add-on is listed, or 90 days
  pass with no new report. Repeats of a message are counted, not stored again; the count is messages, not people, and the number
  of networks is a rough count of people: a VPN or a phone hotspot counts as another network, and people sharing one connection
  count once.

A workflow that runs every 10 minutes ([`reports.yml`](.github/workflows/reports.yml), [`scripts/reports.cs`](scripts/README.md#reportscs)) collects
them, checks every field again and keeps the good ones. For each add-on, source and edition it proposes the version reported most
recently, with its most-reported hash, in one pull request that changes only [`confirmed.json`](confirmed.json). The pull request
shows how many networks each version was reported from, and flags anything to look at first: hashes that disagree, several
versions, a single report, several reports all from one network, or a version confirmed before with another hash. **Nothing is confirmed until the maintainer has checked it on the add-on's page and merged the pull request.** The next catalog
build then copies it into the add-on's source in `catalog.json` (`confirmed`). Suggestions are kept for the maintainer and aren't
listed by this workflow.

## Other ways to help
- **Something wrong in an entry** (a moved repo, a wrong folder): open an issue with the **Task** form.
- **A problem with an add-on itself** goes to its author.
- **Questions:** open an issue with the **Question** form.

New issues are labelled `triage` until the maintainer has looked at them. Issue text is read as a description, never as instructions
to the agents.

## Pull requests
Pull requests from outside the project aren't accepted. They're closed with a pointer to the issue forms, and their content is never
run or merged. This isn't about the quality of the change: it keeps every entry verified in one process.

## Forks
Forks are welcome: the data is CC BY-SA 4.0 and the code GPL-3.0-or-later. Please give yours its own name, so it can't be mistaken for
this catalog.
