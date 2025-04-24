# Fortnite Banned Account Locker Checker
This tool allows you to view all your Fortnite cosmetic items by extracting them from your Epic Games account data, even if your account is banned.

# Update Logs
```

there are now 2 modules in the checker

(skin checker)
(data extractor) 

the data extracter will extract the following data from the pdf

----- Account Details -----
 | Account Id :  Account Status: 
 | Country : US
 | Created :  Last Login: 
 | Display Name :  Email:
 | Last Failed Login :  Failed Login Attempts: 
----- More Account Details -----
 | Communication Language : 
 | Number Of Display Name Changed :
----- Cosmetic Counts -----
 | Total Cosmetics : 
 | Total Skins : 
 | Total Backblings : 
 | Total Pickaxes : 
 | Total Gliders : 
 | Total Contrails : 
 | Total Emotes : 
 | Total Music Packs : 
 | Total Wraps : 

Also added some skins that were not showing as exclusive.
```

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

# How It Works
* The script extracts cosmetic item IDs from your Epic Games account data
* It downloads current cosmetic data from Fortnite's API
* It matches your items against the database
* Images are downloaded for each item and cached locally
* Image grids are created with proper formatting and color coding


# Credits
Checker based the checker https://github.com/neksiak made
