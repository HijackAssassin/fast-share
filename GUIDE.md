# FastShare — User Guide

FastShare moves files directly between two PCs, as fast as the connection allows. There is no
cloud and no account: one PC shares folders, the other connects and browses them like Explorer.

---

## 1. Installing and updating

- **First time:** two downloads on teamwolfgamers.online/fast-share, same app either way:
  - **Installer** (`Fast-Share-V<version>-Setup.exe`): installs for your Windows user (no admin
    rights) with a Start menu shortcut, and an optional desktop one. Uninstall from Windows
    Settings → Apps; your settings and connections stay.
  - **Portable** (`Fast-Share-V<version>.rar`): unpack the folder anywhere (Downloads is fine)
    and run `FastShare.exe`.
  If Windows shows "Windows protected your PC", click **More info → Run anyway** (FastShare is
  not signed). If the portable exe won't start at all, right-click the .rar → **Properties →
  Unblock** before unpacking.
- **Updating:** Settings (gear, top right) → **Check for updates** → **Download**. FastShare
  replaces itself and reopens. It looks in two places, in this order:
  1. **Patrick's PC**, if he has the update server open. This is where early builds come from.
  2. Otherwise **teamwolfgamers.online**, the public download. (From 2.4.13.)

  FastShare also checks by itself as soon as it starts, and every hour after that. When
  a newer version exists, **Update available** appears next to the gear; click it to install.
- **Windows Firewall:** the first time a PC starts sharing (from 2.4.11), FastShare adds its own
  firewall rule so other PCs can connect to it. Windows shows **one** "Do you want to allow this
  app to make changes?" prompt: click **Yes**. Without the rule, nobody can connect *to* that PC.
  It asks once per folder FastShare runs from. See [Troubleshooting](#9-troubleshooting) if you
  said No.

---

## 2. The two ways to connect

Every connection has a **host** (the PC being connected to) and a **visitor** (the PC that
types the code or address). The host must be **sharing** (Share tab → switch **ON**).

| Where the two PCs are | What the visitor types | What the host needs |
|---|---|---|
| **Same home network** (same router) | The host's **connection code** (or its local address, e.g. `192.168.1.100`) | Sharing ON, firewall allowed |
| **Different places** (over the internet) | The host's **connection code**, e.g. `H0WED9GMA4-K3TZ` | Sharing ON, firewall allowed, **port forwarded** on their router |

Both are typed into the same box: **Connections tab → Connect to a PC → Connect**.

### 2a. Same network (local connecting)

Use this for two PCs in the same house. It never touches the internet, needs no port
forwarding, and runs at full local speed (gigabit Ethernet ≈ 940 Mbit/s).

1. **On the host PC:** Share tab → turn sharing **ON** → **Copy Code**.
2. **On the visitor PC:** Connections tab → paste the code → **Connect**. FastShare first asks the
   local network whether that PC is there. If it is, it connects straight to it; if not, it goes
   over the internet as usual. (Needs 2.4.11 or newer on both PCs.)
3. **On the host PC:** a request pops up. Choose what they may do and click **Allow**.

Typing the host's local address (e.g. `192.168.1.100`) instead of the code still works too.

Easier still: the Connections tab lists **On your network** every PC in the house with FastShare
open (checked when you open the tab and every 5 seconds while it's open): a green dot when it is
sharing, red when it is open but not sharing. Double-click a green one to connect. PCs on 2.4.20
to 2.4.23 appear only while sharing; a PC that has never shared appears after its first share
(Windows Firewall has to let FastShare in first).

Tips:
- You can also find the address yourself: open Command Prompt, run `ipconfig`, and look for
  **IPv4 Address** under your Ethernet or Wi-Fi adapter. You can paste that whole line into
  FastShare; it picks out the address.
- The address starts with `192.168.`, `10.` or `172.16`–`172.31`. If it starts with anything
  else, it isn't a local address.
- On versions before 2.4.11, **don't use the code between two PCs in the same house**. The code
  contains your *public* internet address, so the traffic goes out to your router and straight back
  in. That is slower (often ~100 Mbit/s), and some routers refuse it. 2.4.11 finds the PC locally
  instead.
- Two PCs in the same house share one internet address, so their codes start the same. The part
  after the dash tells them apart: since 2.4.19 every new install gets its own on first launch. A
  PC set up earlier that has never pressed **New Code** has only the ten-symbol part; press
  **New Code** on it once so the two codes differ.
- Guest Wi-Fi networks usually block PCs from seeing each other. Both PCs must be on the main
  network.

### 2b. Over the internet

1. **The host** must forward a port (default **TCP 5201**) on their router to their PC. For Google
   Fiber that's done in the **Google Fiber app** (port forwarding settings). Other routers differ;
   search "port forwarding" plus your router's make.
2. **The host** turns sharing **ON** and clicks **Copy Code** on the Share tab.
3. **The visitor** pastes the code on the Connections tab → **Connect**. The host approves.

**Only one of you needs an open port.** If the visitor can't forward a port (like Dillon), that's
fine: once connected, the host can still browse *the visitor's* folders. See the next section.

### 2c. Browsing the visitor back (share-back)

When someone connected to you also shares folders, they appear under **Browse → Folders Shared
With You**. If they don't, type their code or address and press **Connect**. FastShare notices
they're already connected to you and asks them over their existing connection instead. They
approve it, and their folders appear. From 2.4.9 you can **upload, delete, rename and make folders**
there too, as far as their permissions for you allow. Both PCs need 2.4.9 or newer for this.

---

## 3. Approving people and permissions

When someone connects, the host sees a request with their name and whether their identity is
**verified** (the same PC as last time).

- **Allow this user to connect without requesting:** tick this for people you trust. They'll be
  let straight in next time, as long as it's really their PC.
- **User Permissions** (Share tab, middle column): for each connected person, choose
  **Download**, **Upload**, **Modify** (rename, new folder, move) and **Delete**, plus which
  shared folders they can see. Changes take effect within a few seconds, without reconnecting.
- **Folder Permissions** (Share tab, right column): turn something off for a specific folder,
  for everyone. For example, "no delete" on a folder means nobody can delete anything inside
  it, whatever their User Permissions say.
- In both lists, click a person or folder to open its switches; click it again to close them.
- **New Code** (Share tab, next to Copy Code): makes a new code, so one that got passed around
  stops working. Anyone who tries the old code (including people who connected before) is turned
  away before a request even appears, until you give them the new one. People connected right now
  stay connected. When someone connects with your new code, their Recent entry (and Favorite) for
  you updates to it.
- **Close** a connection: click the **×** at the right of their name in the Browse sidebar (or
  right-click it → **Close**).
- **Rename** someone: right-click their name in the Browse sidebar (or in Recent connections) →
  **Rename…**. The name is yours only; clear it to go back to the name they chose.
- **Kick** removes someone until you restart FastShare. **Block** is permanent; blocked people
  are listed on the Connections tab under Favorites.
- **Block from Recent connections or Favorites:** right-click someone → **Block**. They stay in
  the list marked **Blocked**, so the same right-click → **Unblock** undoes it. Connecting to
  someone you blocked asks first ("You blocked this user, are you sure you want to connect?").

---

## 4. Sharing folders

Share tab → **Add Folder.** Everything inside is visible to people you approve (subject to
permissions). Hidden and system files are never shared. You can add or remove folders while
people are connected; their sidebar updates within about 5 seconds.

---

## 5. Moving files

On the **Browse** tab:
- **Download:** select files or folders → **Download** (or drag them onto your desktop / an
  Explorer window). Choose where they go.
- **Upload:** **Upload**, or drag files from Explorer onto the file list. Needs the Upload
  permission.
- **Delete / Rename / New folder:** right-click. Needs the matching permission. One press of
  Delete removes everything it can, even in a huge folder; anything it can't (usually a file
  another program has open) is listed, so you can close that program and delete again.

While a transfer runs:
- **Size** shows how much has moved: "12.4 GB of 31.6 GB", and the bottom bar "Receiving · 120 files
  left · 12.4 GB / 31.6 GB · 3m 10s elapsed · 4m 00s remaining".
- **Time left** estimates the remaining time from the last 20 seconds of progress.
- **Bottom bar:** "Download <speed> [bar]  Upload <speed> [bar]". The bars are always there and
  fill while that direction is moving. Right-click the bar for **Cancel all transfers** and
  **Shut down PC when transfers finish**.
- **Speed** is shown in megabits per second (the unit internet plans use), e.g. "560 Mbit/s".
  Switch to megabytes in Settings → **Speed shown in**. 1 MB/s = 8 Mbit/s.

Other things worth knowing:
- Identical files (same size and date) are skipped.
- A cancelled transfer removes what it had copied.
- **Either PC can cancel:** right-click the transfer in the Activity Log → **Cancel transfer**,
  or right-click the bottom bar → **Cancel all transfers**. When the other PC started it, it is
  asked to stop and does within a few seconds; its row says "Cancelled by <you>". (Both need
  2.4.25 or newer.)
- **When a connection ends,** the Activity Log (Events) and log.txt say why: they closed
  FastShare, they disconnected, or the connection was lost (nothing from their PC for 20 seconds:
  switched off, network down, or FastShare crashed). A transfer cut off that way ends at once,
  marked "Stopped at 51 GB of 94 GB - Connection closed: lost connection", rather than being
  called received.
- A big file interrupted by a network drop resumes where it stopped next time.
- A download and an upload can run at the same time; more of either wait in a queue.

**If the connection drops** in the middle of a transfer, FastShare reconnects by itself (it keeps
trying for 3 minutes) and carries on with the files that hadn't arrived yet - big files resume
from where they stopped. If a transfer still ends with failed files, right-click it in the
Activity Log → **Retry failed files**; only the missing ones move again.

**Resuming later:** a transfer that didn't finish - the PC switched off, the connection was down
for longer than the automatic reconnect waits, or FastShare was closed mid-way (closing now
*pauses* it and keeps what arrived) - is remembered. The next time you connect to that person,
the PC that started it asks **Resume / Discard**; if you resume, the other PC is asked to
**Allow** it, and it runs only if both agree ("X did not agree to resume the last transfer"
otherwise). Only what is missing moves. Cancelling a transfer on purpose still removes what
arrived and is not offered back. Both need 2.4.29 or newer for the second question.

If the rest no longer fits on the receiving disk, **both** PCs get **Retry / Discard** saying how
much is needed and how much is free. The receiver frees some space in File Explorer; once
**both** press Retry, FastShare measures again - and carries on, or asks again if it still
doesn't fit. Either Discard drops the transfer and the other PC is told (2.4.30 or newer).

**Queue:** each direction runs one transfer at a time and the rest wait. The Activity Log tab
shows them while anything is queued: **Move up / Move down** to change the order, **Remove** to
drop one that's waiting, **Cancel** to stop the one that's running. A waiting transfer has its
own row reading "Queued · position 1" (right-click → **Remove from queue**), and the other PC
sees the same on its side, so they know it's coming (both need 2.4.27 or newer).

**Before a transfer starts,** the other PC's row says what it is waiting for: **Choosing a folder**
(they are picking where to save it), **Choosing files**, **Getting file list** (a big folder can
take a while), **Checking files** (they are deciding about files that already exist), or - if it
won't fit - a failed row saying how much space it needs (both need 2.4.40 or newer).

**"Windows has reserved port 5201"** when turning Sharing on: on PCs with Hyper-V, WSL or Docker,
Windows sometimes sets aside a block of ports that includes FastShare's. The message gives three
commands to run in PowerShell as administrator that keep the port for FastShare for good.

**Not enough space:** before anything moves, FastShare checks the receiving disk - your own
for downloads, the other PC's for uploads (it needs 2.4.28 or newer) - and stops with "Not
enough free space…" rather than failing partway.

**Failed files:** a file that can't be moved - usually because a program has it open (a .blend
open in Blender), or Windows won't allow it - is listed with the reason. Double-click the
transfer (or select it and press Enter) and choose **Failed**, or pick **Failed** in the Activity
Log's view list to see every failed file this session. Close the program, then right-click the
transfer → **Retry failed files**.

**Shut down when transfers finish** (Settings → Transfers, or right-click the bottom bar):
tick it while something is transferring - yours or someone else's. Once everything is done
(including a queued transfer or an automatic retry still to come), it unticks itself and shuts
Windows down - no countdown, so you can walk away. Files that failed don't hold it up (no error
box waits on screen either; they're in the Failed list). A program with unsaved work can still
stop the shutdown. Greyed out when nothing is transferring; never remembered for next time.

**Check files after every transfer** (Settings → Transfers, on by default): both PCs compare a
fingerprint of every file that moved and name any that differ. It reads everything again on
both PCs, so it adds time to big transfers (each PC works through its own copies at full speed:
8 files at once on an NVMe drive, 4 on others). While it runs, the progress bars start again
from 0 for the check (**Checking 42%** in the row, "checking files 42%" in the bottom bar) on both
PCs, so a long check never looks stuck at 100%.

**Activity Log** shows each transfer (double-click one for its full file list). Once a transfer
ends, its Progress column says how: **Completed**, **Some failed**, **Failed**, **Interrupted**
(the connection dropped), **Cancelled**, **Paused** or **Discarded**. **History** keeps every
past transfer. **Stats** shows totals.

---

## 6. How fast should it be?

FastShare opens **8 connections at once**, because on long internet routes a single connection
slows down every time a packet is lost. Realistic ceilings:

| Link | Best case |
|---|---|
| Same house, gigabit Ethernet | ~940 Mbit/s (~110 MB/s) |
| Same house, Wi-Fi | 200–600 Mbit/s, varies a lot |
| Internet, both on gigabit fiber | Up to ~900 Mbit/s, *if the route between you is clean* |

The route between two homes matters as much as either plan. Two gigabit plans don't guarantee
gigabit between them: packets lost along the way cut the speed, often more in one direction
than the other.

**Speed test** (Settings → Troubleshooting → **Run speed test**): measures the line itself with
no disks involved. It tests download over 4, 6, 8, 10, 12 and 16 connections (each twice, and
names the fastest), over 1, upload over 8, and ping idle vs busy. It takes about 75 seconds. Run it when a transfer feels slow:
- A transfer near the speed-test figure → the line is the limit.
- A transfer well below it → one of the PCs is (usually its disk).

---

## 7. Settings

| Setting | What it does |
|---|---|
| Your name | What others see when you connect to them |
| Sharing port | Default 5201. Must match the port forwarded on your router. **Save port** switches to it right away and re-checks whether it is open (press it again after forwarding to check again). Your code includes the port, so a new port means a **new code** - send it to anyone who connects to you |
| Check for updates | Updates from Patrick's PC, or the website when his update server is closed |
| Keep shares excluded from Defender | Stops Windows Defender scanning every incoming file (much faster for thousands of small files). Windows asks you to confirm |
| Closing the window keeps FastShare running | X hides to the tray instead of quitting. With it off, the tray icon only appears while a notification is showing |
| Show Fast Share in my Discord status | On by default. While the Discord app is open, your status shows "Playing Fast Share" with Idle, Sharing folders, or Sending/Receiving files with the percent and speed. Never file names or who you are connected to |
| Check files after every transfer | Both PCs compare every file after it moves (slower; on by default) |
| Shut down this PC when all transfers finish | Only while something is transferring; unticks itself, then shuts down |
| Speed shown in | Megabits per second (default, like internet plans) or megabytes per second (like file sizes) |
| Dev mode | Swaps detailed diagnostic logs with the PC you're connected to. Both need it on. For troubleshooting with Patrick |
| Run speed test | See section 6 |
| Open log | Opens `log.txt`, which helps with bug reports |

---

## 8. Quick checklist when something doesn't connect

1. Is the **host sharing**? (Share tab switch shows **ON**.) Under the switch the host's Share tab
   says who can reach it: **Local connection** (same house), **Remote connection** (internet),
   **Port** (open/closed) and **UPnP**. "Blocked by Windows Firewall" there has a **Fix** button.
2. Same house? Use the **local address** from the host's Share tab, not the code.
3. Different places? Is the host's **port forwarded**? FastShare checks this itself when it
   starts sharing and shows whether the port is reachable from the internet.
4. Is FastShare **allowed through the host's Windows Firewall**? (See below.)
5. Are both PCs on a current version? (Settings → Check for updates.)

---

## 9. Troubleshooting

**"That code is no longer valid"**: they made a new code. Ask them for the current one.

**"Connection failed / No answer" to a PC in the same house**
Almost always Windows Firewall on the **host** PC. From 2.4.11 FastShare fixes this itself when it
starts sharing (the Yes/No prompt). If you said No, or you're on an older version:
1. Windows Security → **Firewall & network protection** → **Allow an app through firewall**.
2. **Change settings** → find **FastShare** (or **Allow another app… → Browse** to
   `FastShare.exe`) → tick **Private** → OK.
3. The firewall remembers FastShare *by its folder*. If you run FastShare from a new folder (a new
   download, say), allow it again.
4. Make sure your network is set to **Private**: Settings → Network & Internet → your
   connection → **Private network**. On a *Public* network Windows blocks incoming connections.

**The address changed.** Home routers can hand out a different local address after a restart.
Use the code instead: it finds the PC on the local network wherever its address is now.

**Connecting to your own PC.** Type `localhost` (or leave the box empty and press Connect) to
browse your own shares.

**Slow transfer.** Run the speed test (section 6). Transfers to a hard drive (HDD) are much
slower than to an SSD; the Activity Log's **Drive Types** column shows which is which.

**"The identity of X has changed".** Normal if they reinstalled FastShare. It would also look like
this if someone were impersonating them, so only say Yes if you expected it.

**Reporting a bug.** Settings → **Open log**, and send `log.txt` along with what you did.
