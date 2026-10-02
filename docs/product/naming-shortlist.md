# Naming Shortlist (P1)

_4 Sep 2026. Supports `decisions.md` P1. Domain checks are a snapshot from this date — re-check before buying._

## Criteria (from P1)

1. Reads as horse-industry to a syndicator, agent or stud — without being a pun.
2. `.com.au` available (primary market); `.com` available or cheaply buyable.
3. Works as an inbound-mail domain: `horsename@name.com.au` has to look sane in a trainer's contacts list, so short and unambiguous when spoken.
4. Not confusable with the incumbents (miStable, Prism) or with David's other brands (Arion, Bluebloods, Stallions Australia).
5. No obvious existing Australian business or trademark in the same space.

## How availability was checked

- `.com` — Verisign RDAP (authoritative).
- `.com.au` — DNS delegation via Google DNS. NXDOMAIN with no nameservers is a strong sign the name is unlicensed, but it is not authoritative; the auDA WHOIS lookup sits behind a CAPTCHA, so **confirm the finalists at whois.auda.org.au or your registrar before buying**.
- Clashes — web search only. **An IP Australia trade mark search (search.ipaustralia.gov.au, class 9 and 42) is still to be done for the final two or three.**

## Shortlist

| # | Name | .com.au | .com | Mail domain feel | Notes |
|---|---|---|---|---|---|
| 1 | **Paddockside** | free | free | `@paddockside.com.au` — clean | Plain, unpunny, says "beside the horses". Reads as racing (the paddock / mounting yard) and as breeding (the paddock at the stud), which suits every segment in `product-outline.md` §2. Only general "paddock" businesses in AU (Back Paddock, Paddock Offices); no software or racing clash found. **Recommended.** |
| 2 | **Stableside** | free | parked (NameBright — buyable, price unknown) | `@stableside.com.au` — clean | Same construction, slightly more trainer-flavoured, which pulls toward miStable's ground. A "Stableside at York Racecourse" hotel (UK) and a US "Stableside Capital" exist; neither is a real clash. Strong second. |
| 3 | **Saddlecloth** | free | taken (Namecheap) | `@saddlecloth.com.au` — a little long | Nice idea: the numbered cloth is how a horse is identified on the day, and this product is about identifying which horse a message belongs to. Generic term for a product sold by every saddlery, so trade mark protection would be thin. |
| 4 | **Horsedesk** | free | parked (NameBright — buyable) | `@horsedesk.com.au` — clean | Says "helpdesk for horses", which is honest about the inbox/queue. Slightly SaaS-generic; "desk" is furniture in most search results. |
| 5 | **Hoofline** | free | taken | `@hoofline.com.au` — short | Short and memorable, a bit more consumer-app in tone. No clash found. |
| 6 | **Stablepost** | free | taken (expires Nov 2026) | `@stablepost.com.au` — clean | "Post" carries the mail meaning; fine but plain. Watch the .com expiry. |
| 7 | **Perhorse** | free | free | `@perhorse.com.au` — odd when spoken | Literally the pitch ("one inbox per horse") and both domains free, but reads as a typo of "per horse" and nobody in the industry would say it. Keep as a tagline, not a name. |
| 8 | **Barrierside** | free | free | `@barrierside.com.au` | Racing-only (barriers mean nothing at a stud). Free on both, but narrows the product to segment one. |
| 9 | **Halterbook / Foalbook** | free | free | fine | "-book" names read as social apps and date quickly. Foalbook is breeding-only. Parked. |

## Rejected, with reasons

- **Strapper** — the best single word on the list (the strapper is the person who actually knows the horse), but `strapper.com.au` is Strapper Surf in Torquay, an established Australian brand, and `strapper.com` is held in Korea. StrapperHQ is free on both TLDs but would trade on someone else's Australian name.
- **Homestraight, Nearside, Stablelane, Paddockpost, Furlong, Birdcage, Mountingyard, Stablehand, Fetlock, Blinkers, Silks** — registered on `.com.au` and/or `.com`.
- **Stablemate, Horsetalk, Trackside, Bloodline, Stablebook, Racebook** — in use by others.
- Anything with *stable* as the first word drifts toward miStable; anything with *Arion*, *Blue* or *Stallion* collides with existing projects.

## Recommendation

Take **Paddockside** as the working name unless a validation call throws up a reason not to. It passes every P1 criterion today, both exact-match domains are free, it is segment-neutral, and it says what the product is from the customer's seat rather than the trainer's. Register `paddockside.com.au` and `paddockside.com` (and `paddockside.au` if offered) before the validation calls so the name can be used on the deck; the cost is trivial and the downside of losing it is not.

Keep **Stableside** as the fallback and do not use either name in code until the trade mark search is done (P1 rule).

## Next steps

1. David: confirm `paddockside.com.au` at whois.auda.org.au (CAPTCHA) and run the IP Australia search for "Paddockside" and "Stableside".
2. If clean, register both TLDs and close P1.
3. Update `product-context.md` and this file; the name then flows into the mail-domain design (P11) as `<horse>@<tenant>.paddockside.com.au` or a tenant-owned subdomain.
