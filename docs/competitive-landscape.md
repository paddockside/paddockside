# Competitive Landscape — miStable and Prism

_2 Sep 2026. What the two incumbents actually do, what they validate in our design, and where the genuine gap is._

---

## 1. miStable

**Positioning:** "Horse Trainer Communication Software, Syndication, Studs, Mobile App." An all-in-one solution for horse care, training and operations. Multi-code — thoroughbred, standardbred, equestrian.

**Who it is built around: the trainer.** That is the single most important thing about it.

### Named modules

Professional Communications · Automated Racing Data · Custom Websites · Invoice With Xero Integration · Horse Management · Owner Software & App · Friendly Support · Service-Oriented Cloud Computing

### What the trainer gets

- Share high-definition media, or **dictate text reports on the move**, from a phone
- Automated race notifications — **nominations, weights, acceptances, scratchings, results**
- Training programs and horse location management
- Xero billing, WordPress websites

### What the owner gets (owners.mistable.com)

- "Trainer updates" — "Photos, audio & video straight from the stable"
- "Instant push notifications", "Video & Audio reports", "Direct communication from trainers"
- Upcoming nominations, acceptances and fields; results and replays immediately after the finish
- **"Invoices & Payments"** — monthly statements, expense tracking, secure payment by direct debit or credit card, instant receipts
- Single sign-on dashboard, web plus native iOS and Android apps

---

## 2. Prism (prism.horse)

**Positioning:** "the world's most advanced horse racing management platform." Broad operational ERP for the industry, sold by the module. Australian — Xero and MYOB integrations, plus Breedr.

**Who it is built around: the business.** Trainers, owners, studs, agistment and breaking operations, syndicators.

### Modules and pricing

| Module | |
|---|---|
| Stable Management | scheduling, trackwork, vet procedures, bookings |
| Finance | expenses, invoices, owner splits, payments |
| Website | white-label promotional site |
| Communications | owner/supplier updates via photos, videos, newsletters |
| HR | tasks, timesheets, payroll, recruitment |
| Agistment + Farm Management | farm operations |
| Breeding Management | stallion and broodmare |

Priced per module per month, banded by herd size: Starter (1–14 horses) from $30, Light (15–49) from $90, Premium (50–99) from $125, Ultimate (100+) from $150. Setup and onboarding extra; 14-day trial.

### Syndicator pitch

"The next-gen race horse owner management tool for syndicators" — manage owners, horses, accounts and staff in one system; deep horse profiles and historical data; "deliver communications & email campaigns with professionalism & ease"; mobile app.

Claims $2.3B in transactions billed and "get paid 2 times faster" — the marketing centre of gravity is **billing**, not communication.

---

## 3. What this validates

**Horse lifetime records as the spine.** Prism sells "horse lifetime records" and horse profiles carrying health, movement and transaction history. Our horse-as-aggregate-root decision is the industry-normal shape, not an invention.

**The fact feed is exactly right — and I under-specified it.** miStable automates *nominations, weights, acceptances, scratchings, results*. Our `FACT_RECORD` list should explicitly include **weights** and **scratchings**; scratchings in particular are the canonical case for supersession, which we already handle.

**Voice and video from the trainer's phone is proven, not aspirational.** miStable's trainers already dictate reports and send HD media on the move. Trainers *will* adopt a low-friction capture path. Our per-horse email address is a lower-tech version of the same idea, and worth testing against a simple capture screen later.

**Media is a headline feature, not an attachment.** Both sell photos and video prominently. D14 was right.

**A public/marketing site belongs in the platform.** Both bundle one — miStable via WordPress, Prism as white-label. Our scope already includes it.

---

## 4. Where the gap is — and it is a real one

**Both products are outbound broadcast tools.**

miStable: trainer → owner. Prism: business → owner, explicitly "email campaigns". Both are excellent at pushing updates out, with media, to a list of people.

**Neither solves the problem you actually described.** Nothing in either product captures the *inbound* side — an owner's reply arriving on the wrong email chain, a trainer's text, a race club's email — and files it against the right horse and the right race. Prism's answer to communication is a campaign tool. miStable's is a one-way feed from the stable.

That is the whole of your original brief:

> Right horse, right race, wrong email chain, gets missed.

The four-tier attribution model, the per-horse inbox, the pending queue, and one event stream that holds both directions — **that is the differentiator**, and neither incumbent appears to have it. It is worth being deliberate about protecting that focus rather than drifting toward feature parity on stable management, HR and farm ops, which is where Prism will always win.

---

## 5. What this raises for us

### 5.1 The statements gap is now concrete

miStable's owner portal carries **invoices, monthly statements, expense tracking, direct debit and credit card payments, instant receipts**. Prism's centre of gravity is billing.

We have decided payments and statements are out of scope (D2). If Laurel Oak's owners currently receive statements through miStable, **switching removes something they have today.** That is not necessarily wrong — but it is a decision to make with eyes open, not to discover at migration.

Three options, in increasing cost:

1. Statements stay wherever they are now; the portal links out to them.
2. The portal *displays* statements as documents delivered into the stream, with payment handled elsewhere. Read-only, no payment rails, low cost.
3. Full billing. Explicitly rejected by D2.

Option 2 is worth considering — it keeps the "one place for everything about my horse" promise without taking on payments.

### 5.2 Owner expectations are set by an app with push

Both competitors ship native iOS and Android apps with instant push notifications. Owners coming from miStable will expect their phone to buzz when the horse is accepted, and to open a real app.

Blazor WASM as a **PWA** can be installed to the home screen and can receive web push, including on iOS 16.4+ for home-screen-installed apps. That is probably sufficient, but it is a decision to take deliberately — with a fallback of SMS for the moments that genuinely matter (acceptance, scratching, result) rather than relying on push alone.

### 5.3 The structural reason miStable doesn't fit Laurel Oak

miStable is built with the **trainer** as the account holder; the owner app is downstream of the trainer. Laurel Oak sits *between* trainer and owner — you are the manager and the client relationship is yours.

In a trainer-centric platform, your communications either compete with the trainer's or depend on the trainer's subscription, and the owner relationship is mediated by someone else's software. That is a structural mismatch, and it is a stronger argument for building than any feature comparison.

### 5.4 Migration

miStable holds owner records, communication history and, importantly, **media**. Before any switch we need to know what can be exported and in what form — particularly whether photos and video come out at original quality with dates and horse associations intact.

---

## 6. Buy versus build, honestly

Prism is the closest commercial product to what we are designing and is priced modestly. An honest reading:

- **Prism does more than we plan to build** in stable management, finance, HR and farm operations — none of which is in scope.
- **Prism does not do the one thing this project exists for.** Its communications module is broadcast plus campaigns.
- Building means owning the event model, the attribution engine and the ownership ledger — which is precisely where the value is, and precisely what no vendor is offering.

Worth a look at Prism's Communications module in a trial before committing, purely to confirm it is what the marketing suggests.

---

**Sources:** [miStable](https://mistable.com/) · [miStable Owners Portal](https://owners.mistable.com/) · [Prism](https://www.prism.horse) · [Prism features](https://www.prism.horse/features) · [Prism pricing](https://www.prism.horse/pricing) · [Prism for syndicators](https://www.prism.horse/solutions/syndicators)
