# Changelog

## 0.1.27
* Fixed Steam players, including yourself, still being listed under "Cannot be checked" after 0.1.26. The mod now recognises Steam players by their Steam ID, which the game assigns to every player on Steam, so the list only shows players it really can't check, such as Epic players. Copying a verified player's name no longer affects who is listed.
* A Steam player in the lobby whose mod report never arrives is now held to the check and shown as "no report received", instead of being listed as unable to be checked.

## 0.1.26
* Fixed the compliance panel listing every player under "Cannot be checked" when your game couldn't read players' platforms, which can happen when crossplay login fails or hasn't finished. Steam players already verified by the mod, and you yourself, are no longer listed there a second time or counted twice in the player total. The list is back to showing only the players the mod really can't check, such as Epic players.

## 0.1.25
* 2v2 now has its own rules: a 30 second shot countdown and max time based on par turned on. 3v3, 4v4 and singles are unchanged. Matches from the 2v2 queue pick this up automatically.
* Ruleset enforcement is now on by default. Pressing NO RULESET turns rules off for the current lobby only and no longer changes your saved settings, so a single click can't leave every future match unenforced — previously it stayed off for good, and a host could unknowingly run ranked matches with no rules applied. Matches created by SBGL matchmaking ignore the button and always play by the league rules.
* The "Apply Rulesets" setting remains as the full off switch if rule enforcement itself misbehaves. Turning it off stops the mod touching match rules at all, including matches from the queue, and the Driving Range panel says so in its title so it can't be mistaken for a fault.
* The Ranked tooltip now lists the banned courses instead of counting approved ones, matching how the pool actually works: everything that isn't banned is in play, including courses added by game updates.
* A course name sent by the website for a ranked match is now only rejected if that course is banned. Previously anything missing from the mod's own list was replaced, including new courses.
* The selected ruleset now stays put between rounds, so a run of 2v2s back to back doesn't need the button clicked again after every match. It resets to Ranked when you return to the main menu.
* Added a 2V2 button to the Driving Range ruleset panel for running 2v2 outside the queue. It applies the 2v2 rules and the match counts as an official 2v2, uploading with Red/Blue rosters in the same way as a queued one. The upload is refused if the in-game teams aren't two a side, so a lobby left on 2V2 by mistake can't submit a broken result.
* The mod now runs on both the current game build and the 1.2.2-691 playtest from one install. The playtest renamed several things the mod hooks into, which previously stopped it loading properly: the lobby name API, the way hosting is started, and the player name on the scoreboard. The mod now finds whichever the running game has, and says which it found in the log.
* Compliance checking now works with crossplay. Every player in the lobby is listed, and the mod identifies each one's platform from the game itself rather than guessing. Console players are shown as "Console — mods not possible" and aren't flagged, since they can't install mods. Steam players are now identified by their real Steam ID, so a Steam player is held to the mod check whether or not the mod saw them join a Steam lobby.
* Players on a PC platform the mod can't reach are listed under "Cannot be checked". They aren't marked as passing, because they can install mods and no report can be verified for them.
* Fixed the mod failing to detect the lobby name on the playtest, which left league lobbies unrecognised and stopped ranked lobbies being renamed. The name is now read from the game directly rather than from Steam lobby data, which the playtest no longer publishes.
* Mods that aren't on the approved list are now named. The compliance panel shows "✗ ModName (UnknownGuid)" on your own row, the "ILLEGAL MODS DETECTED" banner lists the mods responsible, and the name is written to the log. Previously only tampered mods were named, so a mod that simply wasn't approved produced a warning with nothing to act on.
* Your own row in the compliance panel now turns red for an unapproved mod, and that mod is marked with a red ✗ in your mod list. It previously showed a green tick while other players in the lobby saw you flagged.
* A player whose own scan failed is now shown as failed rather than as a warning, and counts as non-compliant. This covers what only their client can see, such as a suspicious assembly.

## 0.1.24
* Added Season 3. The mod now follows the season set on SBGLeague.com and applies that season's rules automatically, so the switch happens on the site rather than needing a mod update. If a season has no rules in the mod yet, the previous season's rules carry over.
* Season 3 ranked and team matches are 12 holes, up from 9.
* Season 3 map pool: Vertigo returns to the ranked rotation and Uptown is banned. Central Park and Showdown remain banned.
* The Pro Series button has been removed from the Driving Range ruleset panel for Season 3.
* Fixed matchmaking not working on Super Battle Golf 1.2.2. The game update changed a method the hit tracker hooks into, and the resulting error stopped the matchmaking background sync (queue checks, match polling, season lookup) from ever starting. Hit tracking now works with the new game version, and a hook that fails to attach can no longer stop matchmaking from starting.
* The queue panel now shows the season's name, e.g. "Season 3", instead of "Active Season".
* The staff list is now always read from its latest version, so staff changes take effect without a mod update.
* Players in your lobby who fail the compliance check are now named on screen below the "ILLEGAL MODS DETECTED" banner, e.g. "PlayerName is non compliant!". Every player sees this, not only staff.
* Players who never send a mod report are now marked as failed ("no report received - mod not installed?") instead of staying pending for the whole lobby.
* If the approved mod list fails to download, other players now show as unverified. Previously every player in the lobby was flagged for illegal mods.
* Your own row in the compliance panel now reflects your current scan. It could previously show green while the "ILLEGAL MODS DETECTED" banner was up.
* Players who leave the lobby are removed from the non-compliance banner.
* Players who appeared as a 17-digit Steam ID in the compliance panel now show their Steam name once Steam provides it.
* Compliance warnings now sit above the stats card instead of overlapping it when the card has been moved.
* Repeated compliance errors and timeout warnings are now logged once per change instead of every few seconds.
* Only 0.1.24 is on the approved mod list, so players still on an older version will show as non-compliant until they update.

## 0.1.23
* Basic Fixes

## 0.1.22
* Mod compliance is now verified independently by each player rather than taken on trust. Every client reports the identity and file fingerprint of the mods it has loaded, and the receiving player checks those against the approved mod list themselves. Previously a player's compliance status was accepted as reported, so a mod could present itself as something it was not.
* The compliance panel now shows a verified result per mod instead of matching on the reported name. Players running an older version of this mod appear as "unverified" rather than passing silently, since their reports cannot be checked.
* This mod now verifies its own file against the approved mod list. The approved list already recorded a fingerprint for it, but that check was never actually applied.
* All match, queue, and session uploads now go through the SBGLeague.com mod gateway instead of writing to the database directly. Direct writes were disabled server-side, which is why recent uploads were failing.
* A match and all of its player entries are now submitted together in a single call. Resubmitting the same match is safe and will no longer create duplicates.
* Added ranked team matches. A 2v2 / 3v3 / 4v4 selector appears above the Join Queue button, and results upload with Red/Blue rosters and team scores read from the in-game team assignment.
* Team match results are uploaded once at the end of the match so the final team scores are recorded.
* Your clan tag now appears in front of your name on the player stats card, e.g. [COX2] KingCox22. It also shows when spectating another player, and is hidden for players who aren't in a faction.
* Clan tags also show on the SBGL scoreboard, in front of each player's name.
* The player name on the stats card now keeps its real casing instead of being forced to all caps.
* Fixed player stats not loading on the card for players whose in-game name differs in capitalisation from their SBGLeague.com name (e.g. "JaBoB" in game vs "JaBob" on the site). The card showed "Not Registered" with empty stats even though the scoreboard resolved them fine. Player lookups are now case-insensitive everywhere.
* Names are now displayed using the capitalisation registered on SBGLeague.com rather than the in-game spelling.

## 0.1.21
* Central Park, Showdown, and Vertigo are now banned from ranked for the rest of Season 2 and will no longer appear in the ranked map rotation.

## 0.1.20
* F9 now hides and shows the stats panel. It previously toggled a mod list that is no longer displayed, so the key appeared to do nothing.
* The mod list section has been removed from the compliance UI. Illegal mod and missing mod warnings still appear as before.
* The approved mod list is now fetched even when no player ID is linked, so compliance checking works before you connect your SBGLeague.com account.

## 0.1.19
* Fixed match upload finish positions being sorted by adjusted score (Season 1 formula) instead of base score. Placements now correctly reflect Season 2 rules.

## 0.1.18
* Player card ranking now only counts active players who have played at least one match, so your rank reflects your standing among real participants rather than all registered accounts.
* Fixed profile pictures not loading for some players due to a missing SSL certificate bypass.
* Fixed non-square profile pictures being squished — they are now center-cropped to fit the avatar frame correctly.
* The region/SBGL badge icon is now displayed at full size and maintains correct 1:1 aspect ratio.

## 0.1.16
* Added toggle between the SBGL leaderboard and the native in-game scoreboard. Press F8 (configurable under LiveLeaderboard.UI in mod settings) to switch views. The preference persists between sessions.
* The native scoreboard is now the default. The SBGL leaderboard no longer activates automatically based on lobby name.

## 0.1.15
* Added in-game poll system. Press F7 to open the poll creator; use F1–F4 to vote while a poll is active. Polls sync across all lobby players via P2P and auto-close 20 seconds after voting ends. The poll window is draggable and defaults to the right side of the screen.
* Live leaderboard footer "SBGLeague.com" text is now green, bold, and larger for better visibility.

## 0.1.14
* Fixed host not uploading all players' match results — only the host's own entry was being submitted due to an API query format mismatch. All players registered on SBGLeague.com will now have their scores uploaded by the host.
* Fixed Wind ruleset not applying correctly in ranked matches — the dropdown was not being updated, causing wind to stay at "Low" instead of "Moderate".
* Live leaderboard now shows current hole progress (e.g. 1/9 through 9/9).
* Live leaderboard now shows the current hole name.
* Added SBGLeague.com to the leaderboard footer.

## 0.1.13
* Casual matchmaking is now available to all users. A Ranked/Casual toggle button appears above the Join Queue button.
* Fixed match results not uploading to the site correctly.
* Fixed season ID being wrong on uploaded matches.
* Fixed matches being uploaded twice in some cases.
* Scores now push to the site after each hole instead of only at the end of the match.
* Fixed pre-match and post-match MMR not being recorded on match entries.
* Live leaderboard now updates instantly when the scoreboard changes rather than on a delay, and now includes spectators.
* Fixed matchmaking queue sending the wrong match type value for ranked matches.
* Fixed match detection (CheckForMatch) failing due to a database query incompatibility.
* Player card position can now be fully configured in the mod settings. X offset moves the card left (negative) or right (positive) from screen center. Y offset moves it up from the bottom.
* Fixed ruleset enforcement being incorrectly active by default for some players.

## 0.1.12
* Added secret menu option to show matrix of who is hitting/getting hit by who.

## 0.1.1
* Fixed approved mod list not updating correctly.

## 0.0.17
* Fixed issue where MMR was uploading with decimals.

## 0.0.15
* Fixed players with 0 game points from being uploaded. This might make the first couple holes look bad, but this should fix spectators being uploaded into the final match results.

## 0.0.14
* When Pro Series is selected matches will no longer be uploaded to the site.

## 0.0.13
* Added a "No ruleset option".
* Added mouse over text to show what rules are being applied.

## 0.0.12
* Added config setting to disable rulesets being applied.

## 0.0.11
* Fixed uploaded data overwriting the first match in a two match series rather than making two entries.

## 0.0.10
* Fixed screenshot upload issues
* Fixes to Post MMR and and MMR Delta.

## 0.0.9
* More Duplicated upload fixes

## 0.0.8
* Fixed the SBGL tab being named "controls".
* Increased image upload resolution so that you can actually read it.

## 0.0.7
* Fixed (hopefully) duplicate uploads of Matches
* Added feature to upload image with screenshot automatically to matches automatically uploaded.

## 0.0.6
* Fixed issue where uploaded matches would only submit the hosts information and not everyones.
* Added some additional security checks for mods to prevent tampering.

## 0.0.5
* Fixed issue where Pro Series Matches had White Flags enabled.
* Added config option to hide user stats UI window.
* Added a sound for when the user needs to accept a match made match.
* Added feature to have mod auto upload match progress under certain SBGL conditions.

## 0.0.4
* Fixed issue where mod wouldn't pick up the queue from the website if it was started there first.
* Removed debugging config options that are not needed.

## 0.0.3
* Fixed issue where player could join queue and then start a match leaving them in a limbo state.
* Removed inadvertent listing of players in the queue.

## 0.0.1
* Initial Release
