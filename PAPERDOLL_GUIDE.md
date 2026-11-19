# 🧩 Paper-Doll v1.0 – Guide för `Girl`-skinnet

> **Scope:** `Default`, `Green` och `Pink` behålls som enkla 1-PNG-skins (de får ändå 60 FPS andning, svaj, studs, gravitation och partiklar via Motion 2.0).  
> Denna guide gäller **exklusivt `Girl`-skinnet** (`SlimeHelper/Images/Girl/`) som är vårt fulla Paper-Doll-skin.

---

## 1. Vad Slime-motorn REDAN gör automatiskt

Innan du ritar eller delar upp en emote – kom ihåg att koden redan animerar detta i **60 FPS**:
1. **Andning & Humörhastighet:** `ScaleX`/`ScaleY`/`TranslateY` skalas automatiskt med `Mood` och `Energy`.
2. **Kroppssvaj & Drag-tröghet (`SkewTransform`):** Botten står fast medan kroppen svajar och lutar vid dragning.
3. **Studs, Gravitation & Hopp:** Vid klick (`Poke`), släpp efter dragning och autonoma skrivbordshopp.
4. **Prat-guppande + Läppsynk:** Växlar automatiskt mellan `mouth-open.png` och `mouth-closed.png` när hon pratar, och visar `mouth-smile.png` när hon är glad (`Mood >= 55`).
5. **Ögon-tracking mot muspekaren (endast i `IDLE`):** `eyes.png` flyttar sig mjukt ovanpå `eye-white.png` mot musen i `IDLE`. I alla andra emotes hålls blicken centrerad på den pose du ritat så att uttrycken aldrig ser osynkade ut.
6. **Automatisk blinkning:** `eyes-blink.png` spelas automatiskt var 3–6:e sekund inunder håret i alla Paper-Doll-emotes som har en `eyes-blink.png` (när hon inte pratar, sover eller är mitt i en interaktion — undantaget `THINKING` där hon blinkar medan hon tänker).
7. **Hårsvaj:** `Hair-left.png`, `Hair-mid.png` och `hair-right.png` gungar mjukt med egen fasförskjutning.
8. **Partikeleffekter (`EmoteCanvas`):** `💤` (`SLEEP`), `🎉` + `✨` (`HURRAY`), `✨` (`STREAK`), `💖` (`CUTE`), `💢` / `💫` (`POKE` / `SCARED`).

---

## 2. Exakta Filnamn & Lagerordning som WPF-motorn läser

Lägg lagren i `SlimeHelper/Images/Girl/<EMOTE>/Layers/`. Appen känner igen dessa filnamn automatiskt (skiftlägesokänsligt, `.png` eller `.PNG`, med eller utan `01_`-prefix):

| Z-ordning (nerifrån och upp) | Filnamn i `Layers/` | Vad lagret ska innehålla |
| :--- | :--- | :--- |
| 1 *(Längst bak)* | `base.png` *(alt. `Background Merged.png`)* | Kropp, kläder, hår bakom huvudet, ansikte med **tomma ögonhålor** (utan ögonvita, pupiller, näsa, mun, ögonbryn, lugg) |
| 2 | `eye-white.png` | Ögonvitorna (sclera) så pupillerna har en ren yta att glida på |
| 3 | `eyes.png` *(alt. `eyes-open.png`, `eyes-closed.png`)* | **Endast** pupiller, iris och ljusblänk *(följer muspekaren i `IDLE`)* |
| 4 | `nose.png` | Näsa / näsblänk |
| 5 | `mouth-closed.png` | Stängd mun *(används vid vila/lägre humör + läppsynk)* |
| 5 *(alt)* | `mouth-open.png` | Öppen mun *(växlas in när hon pratar)* |
| 5 *(alt)* | `mouth-smile.png` | Leende mun *(visas när `Mood >= 55` och hon inte pratar)* |
| 6 *(vid blink)* | `eyes-blink.png` | Stängda ögonlock *(ersätter `eye-white` + `eyes` under 140 ms blink)* |
| 7 | `eyebrows.png` | Ögonbryn |
| 8 | `Hair-left.png` | Vänster lugg/hårslinga *(gungan + klipps framför ögonen)* |
| 9 | `Hair-mid.png` | Mittenlugg *(gungan + klipps framför ögonen)* |
| 10 | `hair-right.png` | Höger lugg/hårslinga *(gungan + klipps framför ögonen)* |
| 11 *(Längst fram)* | `overlay.png` *(alt. `<namn>-1.png`)* | *(Valfri)* Statisk effekt/accessoar framför allt annat (t.ex. glasögon, penna/block) |
| 12 *(Animerad effekt)* | `<namn>-1.png`, `<namn>-2.png`, ... | *(Valfri)* Animerade effekt-frames i ~5,5 FPS (t.ex. `warning-1..3`, `heart-1..3`, `drops-1..2`) |
| 13 *(Kroppsrörelse)* | `slimemotion-<g>-<f>.png` | *(Valfri)* Upp till 3 parallella rörelsegrupper (`g = 1..3`, `f = 1..N`) som växlar frames (t.ex. `slimemotion-1-1`, `slimemotion-1-2`) |

> **Krita-tips:** Exportera alltid med **`Adjust export size to layer content` AV** och exakt samma pixelbredd/höjd på alla lager i samma emote!

---

## 3. 💡 Snabbspåret: Återanvänd `IDLE/Layers/`!

### 💡 Snabbare jobb för övriga emotes (Tack vare `IDLE`-only eye tracking)
Eftersom ögonen **bara** följer musen i `IDLE` behöver du **inte** separera `eye-white.png` och `eyes.png` i alla andra emotes om du inte vill – låt gärna ögonen sitta kvar i `base.png` på dem och separera enbart:
- `base.png` (kropp + ögon + ansikte utan mun)
- `mouth-closed.png` / `mouth-open.png` / `mouth-smile.png` *(för läppsynk)*
- `Hair-left.png` / `Hair-mid.png` / `hair-right.png` *(för hårsvaj)*
- `eyes-blink.png` *(valfri om du vill att hon ska blinka i den emotes)*

För emotes där `Girl` har **samma grundpose** (t.ex. `THINKING`, `CUTE`, `ANNOYED`, `FUNNY`, `UNSURE`, `WARNING`, `PUSH`) behöver du **inte rita om hela kroppen**!  
Kopiera bara filerna från `IDLE/Layers/` till `<EMOTE>/Layers/` och byt enbart ut de 1–3 lager som ger uttrycket (oftast bara `eyebrows.png`, `mouth-*.png` och ev. `eyes.png`).

---

## 4. Status & Nästa Steg för ALLA 19 Emotes (`Girl`)

### ✅ Redan klart
1. **`IDLE`** *(Standardläge)*  
   - **Status:** ✅ **Färdig!** (`base`, `eye-white`, `eyes`, `eyes-blink`, `nose`, `mouth-closed`, `mouth-open`, `mouth-smile`, `eyebrows`, `hair-left`, `hair-mid`, `hair-right` på plats).
2. **`ANNOYED`** *(3–4 pokes, svordomar, `!important`, fil raderad)*  
   - **Status:** ✅ **Färdig!** (`base`, `eyebrows`, `hair-left`, `hair-mid`, `hair-right`, `mouth-closed`, `mouth-open`, `nose` på plats).
3. **`FUNNY`** *(YouTube/Reddit-distraktion, Discord/Slack, `127.0.0.1`, skin-byte)*  
   - **Status:** ✅ **Färdig!** (`base`, `eyebrows`, `hair-left`, `hair-mid`, `hair-right`, `mouth-closed`, `mouth-open`, `mouth-smile`, `nose` på plats).
4. **`PUSH`** *(Opushade git-commits, `return null`)*  
   - **Status:** ✅ **Färdig!** (`base`, `eyebrows`, `hair-left`, `hair-mid`, `hair-right`, `mouth-closed`, `mouth-open`, `nose` på plats).
5. **`THINKING`** *(AI-svar pågår i 5–30+ sek, GitHub-test, minnes-tankar, API-testning/Webbläsare)*  
   - **Status:** ✅ **Färdig!** (`base`, `eyebrows`, `eyes-open`, `eyes-blink`, `hair-left`, `hair-mid`, `hair-right`, `mouth-closed`, `mouth-open`, `nose` på plats — blinkar nu även medan AI:n tänker!).
6. **`CUTE`** *(Spotify/musik, Teams/Google Meet, `bind repo`, högt humör)*  
   - **Status:** ✅ **Färdig!** (`base`, `eyebrows`, `hair-left`, `hair-mid`, `hair-right`, `mouth-closed`, `mouth-open`, `nose` + `💖`-partiklar på plats).
7. **`UNSURE`** *(Okänt repo, helgarbete, lågt `Mood` < 35)*  
   - **Status:** ✅ **Färdig!** (`base`, `eyebrows`, `hair-left`, `hair-mid`, `hair-right`, `mouth-closed`, `mouth-open`, `nose` på plats).
8. **`WARNING`** *(DBeaver/MySQL, `regex`, `password`/`secret`, saknad `gh` token)*  
   - **Status:** ✅ **Färdig!** (`base`, `eyebrows`, `eyes-open`, `eyes-blink`, `hair-left`, `hair-mid`, `hair-right`, `mouth-closed`, `mouth-open`, `nose` samt animerad **`warning-1/2/3`**-effekt ovanpå!).
9. **`NOTES`** *(VS Code/IDE, Gmail, GitHub, `/todo`, `Focus`-timer, kalender)*  
   - **Status:** ✅ **Färdig!** (`base`, `eyebrows`, `eyes-open`, `eyes-blink`, `hair-left`, `hair-mid`, `hair-right`, `mouth-closed`, `mouth-open`, `mouth-smile`, `nose` samt animerad **`screen-flicker-1/2/3`** på hennes tablet!).
10. **`BREAK`** *(55 min arbetspass, kaffe/mat, `console.log`)*  
    - **Status:** ✅ **Färdig!** (`base`, `eyes-open`, `eyes-blink`, `hair-left`, `hair-mid`, `hair-right`, `mouth-closed`, `mouth-open`, `nose` samt animerad **`heart-1/2/3`**-effekt!).
11. **`HURRAY`** *(Avklarad To-Do, grön CI-build, Vercel deploy)*  
    - **Status:** ✅ **Färdig!** (`base`, `eyebrows`, `eyes-open`, `hair-left`, `hair-mid`, `hair-right`, `mouth-closed`, `mouth-open`, `nose` samt animerad **`heart-1/2`** + `🎉`/`✨`-partiklar!).
12. **`POKE`** *(1–2 klick på Slime)*  
    - **Status:** ✅ **Färdig!** (`base`, `eyebrows`, `eyes-open`, `hair-left`, `hair-mid`, `hair-right`, `mouth-closed`, `mouth-open`, `nose`, statisk **`warning-1`** + animerad **`drops-1/2`**-effekt!).
13. **`STREAK`** *(Coding-streak, Terminal/Bash, GitHub Actions, `1337`, `sudo`, fredag)*  
    - **Status:** ✅ **Färdig!** (`base`, `eyes-open`, `eyes-blink`, `hair-left`, `hair-mid`, `hair-right`, `mouth-closed`, `mouth-open`, `nose` + `✨`-partiklar!).
14. **`DIRTY`** *(Ocommittade git-ändringar / ny fil skapad)*  
    - **Status:** ✅ **Färdig!** (`base`, `eyebrows`, `eyes-open`, `eyes-blink`, `hair-left`, `hair-mid`, `hair-right`, `mouth-closed`, `mouth-open`, `nose` på plats!).
15. **`ERROR`** *(CI-fel, `while(true)`, AI-fel)*  
    - **Status:** ✅ **Färdig!** (`Background Merged` som bas, `eyebrows`, `eyes-open`, `eyes-blink`, `hair-left`, `hair-mid`, `hair-right`, `mouth-closed`, `mouth-open`, `nose` samt animerad **`drops-1..5`**-effekt!).
16. **`SLEEP`** *(AFK > 6 min / "Put to Sleep" / `async`/`await`)*  
    - **Status:** ✅ **Färdig!** (`base`, `eyebrows`, `eyes-closed`, `hair-left`, `hair-mid`, `hair-right`, `mouth-closed`, `nose` samt 3 oberoende animerade **`slimemotion-1/2/3`**-droppar + `💤`-partiklar!).
17. **`TIRED`** *(Sent på natten, måndag morgon, hög CPU >= 85%, `TODO`)*  
    - **Status:** ✅ **Färdig!** (`base`, `eyebrows`, `eyes-open`, `eyes-blink`, `hair-left`, `hair-mid`, `hair-right`, `mouth-closed`, `mouth-open`, `nose` på plats!).
18. **`SCARED`** *(5+ pokes i rad / Window Shake)*  
    - **Status:** ✅ **Färdig!** (`base`, `eyebrows`, `eyes-open`, `hair-left`, `hair-mid`, `hair-right`, `mouth-closed`, `mouth-open`, `nose` + kraftig skakfysik & `💫`-partiklar!).

> 🎉 **ALLA 18 aktiva Paper-Doll-emotes för `Girl` är nu 100 % klara!**

---

### ℹ️ Övrigt
19. **`BLINK`** — Behövs ej som separat mapp eftersom `eyes-blink.PNG` ligger i respektive `Layers/`-mapp. Behåll `01.png` som fallback.
