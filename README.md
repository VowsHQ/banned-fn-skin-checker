# Fortnite Banned Account Locker Checker
This tool allows you to view all your Fortnite cosmetic items by extracting them from your Epic Games account data, even if your account is banned.

# Categories Supported
**this checker scans for exclusives aswell.**
* Outfits (AthenaCharacter)
* Back Blings (AthenaBackpack)
* Pickaxes (AthenaPickaxe)
* Gliders (AthenaGlider)
* Contrails (AthenaSkyDiveContrail)
* Emotes (only dances, doesn't support sprays and emojis) (AthenaDance)
* Music Packs (AthenaMusicPack)
* Wraps (AthenaItemWrap)

# Requirements
* .NET (https://dotnet.microsoft.com/en-us/download)
* Internet connection (to download cosmetics data and item images)

# Installation
* Download source and build it (visual studio needed). or download the release.

# Usage
**Step 1**
1. Request Your Epic Games Account Data (https://www.epicgames.com/account/personal)
2. Go to Epic Games Account Settings
3. Request a copy of your account data
4. Wait for the email and download your data
5. MAKE SURE PDF NAME IS "EpicGamesAccountData"

**Step 2**
1. Run the Skin_Checker.exe
2. Drag your pdf file into the program
3. Enter the PDF password
4. The skincheck will save to Checker Results Folder.

# Known Issues
Pets don't display properly
Some Items may have black/gray background
Text may overlap on certain items with longer names
Not all exclusives are shown

# How It Works
* The script extracts cosmetic item IDs from your Epic Games account data
* It downloads current cosmetic data from Fortnite's API
* It matches your items against the database
* Images are downloaded for each item and cached locally
* Image grids are created with proper formatting and color coding

# Credits
Checker based the checker https://github.com/neksiak made
