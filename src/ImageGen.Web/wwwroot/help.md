# Help

A few tips for getting what you want out of the app. Tap any heading to open it.

## Making something stronger or weaker — ( )

Put round brackets around something to make the picture lean into it more.

- `(red dress)` — a bit more of this.
- `((red dress))` — even more.
- `(red dress:1.3)` — dial it in with a number: above `1` is stronger, below `1` is weaker, so `(red dress:0.7)` tones it down.

## A random choice each time — [ ]

Put a few options in square brackets, separated by `|`, and the app picks **one at random** for each picture.

- `a [red|blue|green] hat` → each picture gets one of the colors.
- Leave a choice blank to make it "sometimes": `[sunglasses|]` gives you sunglasses about half the time.

## One picture of every option — { }

Curly brackets work like the square ones, but instead of picking one, the app makes **one picture of each** option.

- `{cow|chicken|duck}` → three pictures, one of each. Ask for 10 and you get 10 of each.
- Two sets combine: `{cow|chicken|duck} {fat|skinny}` → all 6 mixes.
- If it's about to make a lot, the app shows you the total first.

## Tags, artists, quiet tags, and guide tags — # @ ! ~

- `#word` — a **tag** (a label the app recognizes, like `#red_dress`).
- `@name` — an **artist**, to borrow their style.
- `!word` — a **quiet tag**. It still goes into the picture, but it stays out of the way when the app is suggesting or adding tags for you.
- `~word` — a **guide tag**. The opposite: it never goes into the picture at all, but the app *does* use it when it suggests and adds tags.

**When to use `!`:** if one big, obvious thing would hog the app's suggestions and crowd out your other ideas, put a `!` in front. Write `!pig` and the pig still shows up, but the rest of your prompt gets its say. (This only matters when you're using the 🎲 surprise tags.)

**When to use `~`:** when you want the *kind* of tags that go with one thing, but a picture of something else. This is how you swap the subject.

Write:

    !1girl, ~1boy

and the app goes looking for tags that usually turn up around `1boy` — the poses, the clothes, the way the shot is framed — and then puts all of that on a girl, because the `~1boy` never reaches the picture and the `!1girl` was kept out of the app's way so it couldn't pull everything back toward its own usual tags.

A guide tag is invisible everywhere afterwards. It isn't in the picture, so it doesn't get a chip and you can't bookmark it — the app only ever shows you what actually went in. It also still helps the suggestions that pop up as you type, so `~1boy` starts offering you boy-ish tags right away.

## Keeping things out — the Negative prompt box

Under the prompt there's sometimes a second, smaller box called **Negative prompt**. Whatever you put in it is a list of things you'd rather the picture didn't have.

**It only appears for some styles.** Not every style uses a negative, and the box is only shown for the ones that do. If you can't find it, that's the style you picked, not a missing feature.

**You're adding to what's already there, not replacing it.** Every style that supports a negative already ships with one — a short list of the things that usually go wrong with it. Anything you type is added on top of that list. You can't accidentally turn the built-in one off, and you don't need to retype it.

**Leaving it blank is the normal thing to do.** Blank means "just use the style's own list," which is what it was tuned with. Reach for the box when a specific picture keeps coming out with a specific problem — not as a matter of routine.

**More is not safer.** A negative is not a wish list. Every word in it pulls the picture somewhere, including words for things that were never going to show up anyway, and a long negative full of "just in case" terms makes pictures worse rather than safer. Add the one thing that actually went wrong.

It takes `#` and `@` the same way the main prompt does, so you can name a tag or an artist you want steered away from.

Two other places already respect it: 🎲 **Random prompt** won't pick a tag you've put in the negative box, and 🎨 **Random artist** won't pick an artist you've put there.

The edit page has its own negative box for each of Edit, Inpaint and Outpaint, and they all work the same way.

If it's a thing you *never* want, from any picture, don't type it every time — ban it instead (see below).

## What the chip colors mean

On a saved picture (and on the Bookmarks page) each tag is a little rounded "chip." Its color tells you what kind of thing it is, so a character or a series stands out from ordinary words. You'll see the same colors in the pop-up suggestions.

- <span class="tagchip tag">general tag</span> — an ordinary word.
- <span class="tagchip cat-character">character</span> — a specific **character** (green).
- <span class="tagchip cat-copyright">copyright</span> — the **show / game / series** something is from (purple).
- <span class="tagchip cat-meta">meta</span> — something about the picture itself, like "high quality" (orange).
- <span class="tagchip artist">@artist</span> — an **artist** (their own color).

And two that mean *you* did something:

- <span class="tagchip tag on"><span class="tc-star">★</span>bookmarked</span> — filled in: you **saved** it.
- <span class="tagchip tag banned">banned</span> — red: you told the app to **skip** it.

## The suggestions that pop up as you type

As you type, a little list of matching tags and artists appears — tap one to drop it in.

Once you've got a tag or two written, the suggestions get smarter and offer tags that usually go **with** what you've already typed. The percentage next to each one is just how strong a match it is — bigger is a better fit.

Only `#` tags and `~` guide tags steer the suggestions. Artists don't (asking for an artist's favourite subjects isn't what you meant), and neither do `!` quiet tags — keeping them out of the way is the whole point of writing one.

## Making more than one picture at once

- Tap **Generate** (or press Enter) for **one** picture.
- **Hold** the Generate button to choose how many: **2, 4, 6, 10**, or type your own number.
- While it's working, **Generate stays Generate** — tap it again (or hold for a number) to add more to the queue. A **Cancel** button appears below the progress bar to stop the one in progress.

The same hold-for-a-number trick works on the **Reload** button on a saved picture.

## Using several styles at once

- In the style picker, **press and hold** a style to turn on ticking, then tick as many as you like (a quick tap just picks one).
- It makes your batch in **every** style you picked, so you can compare them side by side.
- Any settings the styles share apply to all of them. The 🎲 and 🎨 options only show up when you've picked a single style.

## Mixing up the shape

**Shape** is normally one choice — Square, Landscape, or Portrait. But you can pick more than one:

- **Press and hold** a shape to **add** it to the ones already lit up. Hold another to add a third.
- With two or more lit up, every picture comes out in **one of them, picked at random** — so a batch gives you a mix.
- A quick **tap** always goes back to just that one shape.

Your choice is remembered, so it's still there next time you come back.

## Surprise tags — 🎲 Random prompt

Slide **🎲 Random prompt** up and the app adds its own extra tags to each picture, on top of what you typed.

- All the way left (**0**) is **off**.
- A little to the right keeps things **safe and sensible**.
- Further right gets **wilder and more surprising**.

Every picture gets its own fresh set, so a batch comes out nicely varied. It builds on the tags you typed, and leaves out anything you've banned or put in the negative box.

### Kinds it may pick — the row of buttons under the slider

Once the slider is above 0, a row of buttons appears underneath it. Each button is one *sort* of tag, and together they decide what the app is allowed to reach for when it makes up its extra tags. A button that's **lit up** is allowed; tap it to switch it off, tap again to bring it back.

- **General** — ordinary describing words: `long_hair`, `smiling`, `raining`, `castle`. This is the bulk of a normal prompt.
- **Character** — a particular named character.
- **Copyright** — the show, game, or series a character comes from.
- **Meta** — notes about the picture itself rather than what's in it, like "high quality" or "traditional media".
- **Artist** — the name of an artist, which pulls the whole picture toward their style. This one usually starts switched off, because 🎨 **Random artist** just below is the tidier way to get one.

They're the same kinds as the chip colors further up this page, so a tag you've seen on a saved picture will come from whichever button matches its color.

**Switching one off doesn't just cross those tags out.** The app builds a different set from the start — one that never needed that kind — so you still get a full, sensible prompt, just made only of the kinds you kept. Turn *everything* off and there's nothing left for it to add, so you'll get exactly the tags you typed and no more.

These buttons only change the 🎲 surprise tags. What you typed yourself always goes in untouched, whatever kind it is, and the suggestions that pop up while you type are unaffected.

Like everything else on the form, your choice applies to the pictures you make **next**: change it and the following batch uses the new kinds, while anything already waiting in the queue comes out the way it was sent. It's remembered for next time, so you only need to set it once.

## A random artist — 🎨 Random artist

Turn on **🎨 Random artist** and each picture gets a random artist's style mixed in — a different one every time.

It adds to your prompt (it won't replace an artist you chose yourself), and it steers clear of any artist you've banned or put in the negative box.

## Saving your favorites

On a saved picture you'll see a **☆ star** and some highlighted tag/artist chips.

- Tap the **☆ star** to save the whole **picture**.
- Tap a highlighted **chip** once to save that **tag or artist**.

Everything you save shows up on the **★ Bookmarks** page in the left menu. There you can **pin** a favorite artist to the top (📌) or remove something with its **×**.

## Sorting your saved things into groups

Want your saved things kept in their own groups, like "outfits" or "poses"?

- **Press and hold** (or **right-click**) the star or a chip to open **Add to categories**.
- Tick any groups you want it in, or type a **new** one — something can be in more than one group at a time.
- Anything you don't put in a group sits under **Global**. The Bookmarks page shows Global first, then your groups.

## Banning tags or artists you don't want

On a saved picture, tapping a highlighted chip steps through three states: one tap **saves** it, a second tap **bans** it, and a third clears it.

Banning tells the app to leave that tag or artist out when *it's* picking things for you — the 🎲 and 🎨 options — just for that style. It never stops you from typing it yourself. You can tidy up your bans under **⚙ Settings**.

## Deleting several pictures at once

To clear out a few pictures in one go:

- **Press and hold** a picture to start selecting.
- **Tap** the others you want. A bar shows how many you've picked, with a **🗑** to delete and a **✕** to cancel.
- It asks you to confirm first.

This works on your history and gallery pages, not on the Bookmarks page.
