# Changelog

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
