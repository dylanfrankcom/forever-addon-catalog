# Contributing

Thanks for helping. This catalog, like [Forever Add-on Manager](https://github.com/dylanfrankcom/forever-addon-manager), is free and
open source and takes no donations. If you'd like to support someone, support the add-on authors.

## How it's made
The catalog is maintained by AI agents (Claude Code) under the maintainer's direction. Every change goes through the same process: an
issue, a branch, validation, and a pull request that CI checks before it's merged. That's why contributions come as **issues, not pull
requests**.

## Asking for an add-on
Ask from [Forever Add-on Manager](https://github.com/dylanfrankcom/forever-addon-manager): its **Help the catalog** page suggests the add-ons you
have installed that the catalog doesn't list, with what their own `.toc` files say, and sends them only when you agree (see
[what's sent](#privacy)). No GitHub account is needed, and there's no issue form for it. Suggestions from several players are counted.
Before an entry is added, the release is downloaded and its `.toc` read, and every
source and link is checked. Only add-ons whose current release lists the Forever Interface are listed.

Some add-ons can't be listed, for example when their only source has turned off downloads by other apps. They get an issue labelled
`unavailable` that says what would make it possible (the [Add-on that can't be listed](https://github.com/dylanfrankcom/forever-addon-catalog/issues/new?template=unavailable.yml) form). Comments there are welcome when something changes.

If it's your own add-on, see [For add-on authors](README.md#for-add-on-authors) too: publishing GitHub releases gives players one-click installs and updates.

## Version reports from the app
CurseForge and Wago don't let the catalog check their files automatically, so for add-ons downloaded from them the catalog relies
on players. With the player's permission, Forever Add-on Manager can report the version it downloaded and the zip's SHA-256, or
suggest an add-on the catalog doesn't list, without a GitHub account. Reports go to a small relay, which checks and counts them and
keeps them until the catalog collects them. The relay never writes to GitHub.

### Privacy
- **What's sent:** for a version report, the add-on's catalog ID, the source (CurseForge or Wago), the game edition, the version
  from the add-on's `.toc` file and the SHA-256 of the downloaded zip, and optionally more of that `.toc`: its title, author,
  interface numbers, notes, category, languages, optional add-ons and the GitHub repo it links to. For a suggestion, the source
  (CurseForge, Wago or GitHub) and project ID, the edition, the
  add-on's name and folder names, and its `.toc` title, version, interface numbers and author. The app asks before sending anything
  and shows exactly what it will send.
- **Never sent or kept:** files (the zip never leaves the player's PC), file paths, account names, machine details or anything else
  about the player.
- **Addresses:** the sender's IP address is never stored. The relay uses it in two ways only:
  - hashed, as the keys of Cloudflare's in-memory rate limiters (one for the address, a looser one for its network), never
    written down;
  - to count how many different networks sent the same message. With that message only, the relay keeps a one-way value made
    from the network part of the address (the first three numbers of an IPv4 address, the first half of an IPv6 one), the
    message and a secret key only the relay holds. It can't be turned back into an address or matched with anything else, it's
    different for every message, so one person's messages can't be linked, and it's deleted with the message. The catalog gets
    only the number of networks, never the values. At most 50 are kept per message.
- **Internet provider:** Cloudflare tells the relay which provider's network a message came through (its autonomous system
  number, shared by all of that provider's customers). The relay keeps, in the same way and with the same limits, a one-way
  value made from that number, the message and the secret key, so it can count how many different providers sent the same
  message. The number itself isn't kept, and the catalog gets only the count.

  Request logging and tracing are off.
- **Limits:** past 20 new versions or checksums for one add-on in a day, or 200 new suggestions in a day, the relay refuses
  more until the next day.
- **How long:** the relay deletes a message 7 days after the catalog collects it, and after 90 days if it's never collected. The
  catalog keeps only the checked fields above, without any address, until the version is confirmed, the add-on is listed, or 90 days
  pass with no new report. Repeats of a message are counted, not stored again; the count is messages, not people, and the number
  of networks is a rough count of people: a VPN or a phone hotspot counts as another network, and people sharing one connection
  count once, and the number of providers is how many different internet providers those networks belong to.

A workflow that runs every 10 minutes ([`reports.yml`](.github/workflows/reports.yml), [`scripts/reports.cs`](scripts/README.md#reportscs)) collects
them, checks every field again and keeps the good ones. For each add-on, source and edition it proposes the version reported most
recently, with its most-reported hash, in one pull request that changes only [`confirmed.json`](confirmed.json) and, for add-ons listed by hand, their files in
[`overrides/`](overrides). The pull request
shows how far each version has come toward what it needs (0, 25, 50, 75 or 100%, never the counts), and flags anything to
look at first: hashes that disagree, several versions, a single report, several reports that seem to come from one player, or a
version confirmed before with another hash. The next catalog build copies what's confirmed into the add-on's source in
`catalog.json` (`confirmed`).

**How a proposal is decided.** A new version is accepted when several players report the same file over at least a day.
Details and new add-ons need more agreement over a longer time, and a Claude review. Anything unusual goes to the
maintainer. The exact rules aren't published, and they can change.

Claude (Sonnet) reviews from the checked facts alone, and can only answer merge, wait for more players, or ask the
maintainer; it runs nothing and changes nothing. Anything unusual also pauses all automatic merging until the maintainer
has looked. Automatic merges are capped per day, can change only `confirmed.json` and add to existing files in
`overrides/` (a guard refuses anything else), and each is a pull request that can be reverted. Everything else waits in
the confirmation pull request, where the maintainer confirms it by merging, or for more reports. A weekly issue lists
what was decided automatically.

**Linking to GitHub.** When an add-on listed by hand has a `.toc` that names its GitHub repo, the catalog looks at the repo
itself (never at CurseForge or Wago) and links the entry to it only on proof that someone else couldn't fake: the repo's own
`.toc` names the add-on's CurseForge project or Wago ID, and either the catalog's own records already name the repo or its
owner, or many players' copies of a version they confirmed name it, all alike, and the repo was there before that version and
isn't a fork or new. A copy of the add-on's file in a repo isn't proof, since anyone could upload one. Then the add-on's versions and details come from its GitHub releases,
and add-on managers can install it from there; its CurseForge and Wago links stay. Anything short of
that goes to the maintainer. A link is withdrawn automatically when the repo is archived, moves to another owner (or its name is taken by another repo)
or stops naming the add-on, and the
withdrawal is published on the entry so add-on managers can warn players who installed it from GitHub; a repo under a new
owner needs the same proof again. A suggested
GitHub add-on the catalog doesn't list is looked at by the discovery workflow straight away, by its usual rules.

For an add-on listed by hand, the same pull request
adds what the `.toc` players' downloads carry says and its entry lacks (editions, languages, optional add-ons the catalog lists),
never replacing what's there, and names what an entry can't hold, such as a second author. Suggestions are kept for the maintainer and aren't
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
