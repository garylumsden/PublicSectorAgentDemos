# Branding assets

Place an SVG/PNG logo here (e.g. `logo.svg`) and set `branding.emblem` in `config/scenario.json` to `branding/logo.svg`. Leave the default emoji emblem if you prefer.

## Avatars

The debate chamber shows an avatar for every persona (seats, the Chair podium, the roll-call, and each
transcript turn) in place of initials. Four role defaults ship in `avatars/`:

- `avatars/chair.svg` — the Chair
- `avatars/moderator.svg` — the Moderator
- `avatars/nexus.svg` — the Nexus Analyst
- `avatars/member.svg` — every debating member

To override per persona, set `avatar` on that persona in `config/scenario.json` — a path served from
`wwwroot/` (e.g. `branding/avatars/my-chair.png`) or an absolute URL. Omit it to use the role default.
Square images work best (they're cropped to a circle).
