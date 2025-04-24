using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FortniteLocker
{
    class Program
    {
        private static readonly string ResultsFolder = Path.Combine(Directory.GetCurrentDirectory(), "Checker Results");
        private static readonly string CacheFolder = Path.Combine(ResultsFolder, "Cache");

        private static readonly Dictionary<string, (string Subfolder, string TextFile, string ImageFile)> Categories = new Dictionary<string, (string, string, string)>
        {
            { "AthenaCharacter", ("Characters", "AthenaCharacter.txt", "AthenaCharacter.png") },
            { "AthenaBackpack", ("Backpacks", "AthenaBackpack.txt", "AthenaBackpack.png") },
            { "AthenaPickaxe", ("Pickaxes", "AthenaPickaxe.txt", "AthenaPickaxe.png") },
            { "AthenaGlider", ("Gliders", "AthenaGlider.txt", "AthenaGlider.png") },
            { "AthenaSkyDiveContrail", ("Contrails", "AthenaSkyDiveContrail.txt", "AthenaSkyDiveContrail.png") },
            { "AthenaDance", ("Emotes", "AthenaDance.txt", "AthenaDance.png") },
            { "AthenaMusicPack", ("Music", "AthenaMusicPack.txt", "AthenaMusicPack.png") },
            { "AthenaItemWrap", ("Wraps", "AthenaItemWrap.txt", "AthenaItemWrap.png") }
        };

        private static readonly List<string> Keywords = new List<string>
        {
            // Account Details
            "Account Id",
            "Display Name",
            "Created",
            "Last Failed Login",
            "Country",
            // More Account Info
            "Communication Language",
            "Headless: false",
            "Number Of Display Name Changed",
            // Cosmetic Counts
            "Total Cosmetics",
            "Total Skins",
            "Total Backblings",
            "Total Pickaxes",
            "Total Gliders",
            "Total Contrails",
            "Total Emotes",
            "Total Music Packs",
            "Total Wraps",
            // Separators
            "----- Account Details -----",
            "----- More Account Details -----",
            "----- Cosmetic Counts -----"
        };

        private static readonly Regex Pattern = new Regex(@"(\bAthena\w+):\s*(.+)", RegexOptions.IgnoreCase);
        private static readonly Regex DatePattern = new Regex(@"\b(\d{4}-\d{2}-\d{2})\b|\[(\d{4}-\d{2}-\d{2})\]|\((\d{4}-\d{2}-\d{2})\)", RegexOptions.IgnoreCase);

        public class ImageDownloader
        {
            private readonly HttpClient _httpClient;
            private readonly string _cacheDir;
            private readonly SemaphoreSlim _rateLimit;

            public ImageDownloader(int maxWorkers = 10, string cacheDir = "image_cache")
            {
                _httpClient = new HttpClient();
                _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
                _cacheDir = Path.Combine(CacheFolder, cacheDir);
                _rateLimit = new SemaphoreSlim(maxWorkers, maxWorkers);

                if (!Directory.Exists(_cacheDir))
                {
                    Directory.CreateDirectory(_cacheDir);
                }
            }

            public async Task<Image> DownloadImageAsync(string url, Size size)
            {
                if (string.IsNullOrEmpty(url))
                    return null;

                string cacheFilename = Path.Combine(_cacheDir, Regex.Replace(url, @"[^\w]", "_") + $"_{size.Width}x{size.Height}.png");

                if (File.Exists(cacheFilename))
                {
                    try
                    {
                        return Image.FromFile(cacheFilename);
                    }
                    catch
                    {
                
                    }
                }

                await _rateLimit.WaitAsync();
                try
                {
                    var response = await _httpClient.GetAsync(url);
                    response.EnsureSuccessStatusCode();

                    using var stream = await response.Content.ReadAsStreamAsync();
                    using var img = Image.FromStream(stream);
                    var resized = new Bitmap(img, size);

                    resized.Save(cacheFilename, ImageFormat.Png);
                    return resized;
                }
                catch (Exception e)
                {
                    Console.WriteLine($"Error downloading {url}: {e.Message}");
                    return null;
                }
                finally
                {
                    _rateLimit.Release();
                }
            }
        }

        private static string NormalizeString(string s)
        {
            if (string.IsNullOrEmpty(s))
                return "";
            return Regex.Replace(s.ToLower(), @"[^a-z0-9]", "");
        }

        private static async Task<(Dictionary<string, List<string>> Items, Dictionary<string, DateTime> PurchaseDates, Dictionary<string, int> CategoryCounts)> ProcessPdfAsync(string filePath, string password = null)
        {
            try
            {
                var data = Categories.Keys.ToDictionary(key => key, key => new List<string>());
                var purchaseDates = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
                var categoryCounts = Categories.Keys.ToDictionary(key => key, key => 0);

                using (var reader = new PdfReader(filePath, new ReaderProperties().SetPassword(password != null ? System.Text.Encoding.UTF8.GetBytes(password) : null)))
                using (var pdfDoc = new PdfDocument(reader))
                {
                    for (int pageNum = 1; pageNum <= pdfDoc.GetNumberOfPages(); pageNum++)
                    {
                        var page = pdfDoc.GetPage(pageNum);
                        var text = PdfTextExtractor.GetTextFromPage(page);
                        var matches = Pattern.Matches(text);

                        foreach (Match match in matches)
                        {
                            string category = match.Groups[1].Value;
                            string details = match.Groups[2].Value.Trim();

                            if (Categories.ContainsKey(category))
                            {
                                string filteredDetails = Regex.Replace(details, @"1$", "").Replace("_", "-");
                                string itemId = filteredDetails;
                                DateTime? purchaseDate = null;

                                var dateMatch = DatePattern.Match(details);
                                if (dateMatch.Success)
                                {
                                    string dateStr = dateMatch.Groups[1].Success ? dateMatch.Groups[1].Value :
                                                    dateMatch.Groups[2].Success ? dateMatch.Groups[2].Value :
                                                    dateMatch.Groups[3].Value;
                                    if (DateTime.TryParse(dateStr, out DateTime parsedDate))
                                    {
                                        purchaseDate = parsedDate;
                                        itemId = Regex.Replace(filteredDetails, @"\s*\[?\(?\d{4}-\d{2}-\d{2}\)?\]?\s*", "").Trim();
                                    }
                                }

                                if (category == "AthenaDance" && !itemId.StartsWith("eid-", StringComparison.OrdinalIgnoreCase))
                                    continue;

                                if (!data[category].Contains(itemId))
                                {
                                    data[category].Add(itemId);
                                    categoryCounts[category]++;
                                }
                                if (category == "AthenaCharacter" && purchaseDate.HasValue)
                                {
                                    purchaseDates[itemId] = purchaseDate.Value;
                                }
                            }
                        }
                    }
                }

                foreach (var category in Categories)
                {
                    var subfolder = Path.Combine(ResultsFolder, category.Value.Subfolder);
                    if (!Directory.Exists(subfolder))
                    {
                        Directory.CreateDirectory(subfolder);
                    }

                    var sortedItems = data[category.Key].OrderBy(x => x).ToList();
                    var outputPath = Path.Combine(subfolder, category.Value.TextFile);
                    await File.WriteAllLinesAsync(outputPath, sortedItems);
                }

                Console.WriteLine("Processing complete. Files created in Checker Results subfolders.");
                return (data, purchaseDates, categoryCounts);
            }
            catch (iText.Kernel.Exceptions.BadPasswordException)
            {
                throw new Exception("Incorrect password for encrypted PDF.");
            }
            catch (Exception e)
            {
                Console.WriteLine($"An error occurred: {e.Message}");
                return (null, null, null);
            }
        }

        private static async Task ExtractInfoToTextFileAsync(string filePath, string password = null, Dictionary<string, int> categoryCounts = null)
        {
            try
            {
                var accountDetailsLines = new List<string>();
                var moreAccountInfoLines = new List<string>();
                var cosmeticCountLines = new List<string>();

 
                using (var reader = new PdfReader(filePath, new ReaderProperties().SetPassword(password != null ? System.Text.Encoding.UTF8.GetBytes(password) : null)))
                using (var pdfDoc = new PdfDocument(reader))
                {
                    for (int pageNum = 1; pageNum <= pdfDoc.GetNumberOfPages(); pageNum++)
                    {
                        var page = pdfDoc.GetPage(pageNum);
                        var text = PdfTextExtractor.GetTextFromPage(page);

                        foreach (var keyword in Keywords.Where(k => !k.StartsWith("Total ") && !k.StartsWith("-----")))
                        {
                            var regex = new Regex($@"\b{Regex.Escape(keyword)} *: *([^\n\r]+)", RegexOptions.IgnoreCase);
                            var matches = regex.Matches(text);

                            foreach (Match match in matches)
                            {
                                if (match.Groups[1].Success)
                                {
                                    string line = $" | {keyword} : {match.Groups[1].Value.Trim()}";
                                    if (Keywords.Take(5).Contains(keyword)) 
                                        accountDetailsLines.Add(line);
                                    else 
                                        moreAccountInfoLines.Add(line);
                                }
                            }
                        }
                    }
                }

  
                if (categoryCounts != null)
                {
                    int totalCosmetics = categoryCounts.Values.Sum();
                    cosmeticCountLines.Add($" | Total Cosmetics : {totalCosmetics}");
                    cosmeticCountLines.Add($" | Total Skins : {categoryCounts.GetValueOrDefault("AthenaCharacter", 0)}");
                    cosmeticCountLines.Add($" | Total Backblings : {categoryCounts.GetValueOrDefault("AthenaBackpack", 0)}");
                    cosmeticCountLines.Add($" | Total Pickaxes : {categoryCounts.GetValueOrDefault("AthenaPickaxe", 0)}");
                    cosmeticCountLines.Add($" | Total Gliders : {categoryCounts.GetValueOrDefault("AthenaGlider", 0)}");
                    cosmeticCountLines.Add($" | Total Contrails : {categoryCounts.GetValueOrDefault("AthenaSkyDiveContrail", 0)}");
                    cosmeticCountLines.Add($" | Total Emotes : {categoryCounts.GetValueOrDefault("AthenaDance", 0)}");
                    cosmeticCountLines.Add($" | Total Music Packs : {categoryCounts.GetValueOrDefault("AthenaMusicPack", 0)}");
                    cosmeticCountLines.Add($" | Total Wraps : {categoryCounts.GetValueOrDefault("AthenaItemWrap", 0)}");
                }

               
                var outputLines = new List<string>();
                if (accountDetailsLines.Any())
                    outputLines.Add("----- Account Details -----");
                outputLines.AddRange(accountDetailsLines.OrderBy(x => x));

                if (moreAccountInfoLines.Any())
                    outputLines.Add("----- More Account Details -----");
                outputLines.AddRange(moreAccountInfoLines.OrderBy(x => x));

                if (cosmeticCountLines.Any())
                    outputLines.Add("----- Cosmetic Counts -----");
                outputLines.AddRange(cosmeticCountLines);

                if (outputLines.Any())
                {
                    var outputPath = Path.Combine(ResultsFolder, "extracted_info.txt");
                    await File.WriteAllLinesAsync(outputPath, outputLines);
                    Console.WriteLine($"Extracted data saved to {outputPath}");
                }
                else
                {
                    Console.WriteLine("No matching keywords found in the PDF.");
                }
            }
            catch (iText.Kernel.Exceptions.BadPasswordException)
            {
                throw new Exception("Incorrect password for encrypted PDF.");
            }
            catch (Exception e)
            {
                Console.WriteLine($"An error occurred while extracting info: {e.Message}");
            }
        }

        private static async Task<bool> DownloadFortniteCosmeticsAsync()
        {
            const string url = "https://fortnite-api.com/v2/cosmetics/br";
            Console.WriteLine("Downloading Fortnite cosmetics data...");

            try
            {
                using var httpClient = new HttpClient();
                var response = await httpClient.GetAsync(url);

                if (response.IsSuccessStatusCode)
                {
                    var data = await response.Content.ReadAsStringAsync();
                    var jsonData = JsonConvert.DeserializeObject<JObject>(data);

                    Console.WriteLine($"Successfully retrieved {jsonData["data"].Count()} cosmetic items");

                    var outputPath = Path.Combine(ResultsFolder, "fortnite_cosmetics.json");
                    await File.WriteAllTextAsync(outputPath, data);
                    Console.WriteLine("Data saved to Checker Results/fortnite_cosmetics.json");
                    return true;
                }
                else
                {
                    Console.WriteLine($"Error: API request failed with status code {response.StatusCode}");
                    return false;
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"An error occurred: {e.Message}");
                return false;
            }
        }

        private static async Task<bool> EnsureCosmeticsDataAsync()
        {
            var jsonPath = Path.Combine(ResultsFolder, "fortnite_cosmetics.json");
            if (!File.Exists(jsonPath))
            {
                Console.WriteLine("Fortnite cosmetics data not found. Downloading...");
                return await DownloadFortniteCosmeticsAsync();
            }
            else
            {
                Console.WriteLine("Using existing Fortnite cosmetics data.");
                return true;
            }
        }

        private static async Task CreateLockerImageAsync(Dictionary<string, DateTime> purchaseDates)
        {
            Console.WriteLine("Starting create_locker_image function...");
            var startTime = DateTime.Now;

            try
            {
                Console.WriteLine("Loading cosmetics data from JSON file...");
                var loadStart = DateTime.Now;
                var jsonPath = Path.Combine(ResultsFolder, "fortnite_cosmetics.json");
                var cosmeticsData = JsonConvert.DeserializeObject<JObject>(await File.ReadAllTextAsync(jsonPath));
                Console.WriteLine($"JSON loaded in {(DateTime.Now - loadStart).TotalSeconds:F2} seconds");

                Console.WriteLine("Creating optimized lookup dictionaries...");
                var dictStart = DateTime.Now;

                var cosmeticsLookup = new Dictionary<string, JToken>(StringComparer.OrdinalIgnoreCase);
                var typeSpecificLookup = Categories.Keys.ToDictionary(cat => cat, cat => new Dictionary<string, JToken>(StringComparer.OrdinalIgnoreCase));

                foreach (var item in cosmeticsData["data"])
                {
                    string itemId = item["id"].ToString().ToLower();
                    cosmeticsLookup[itemId] = item;

                    string itemType = item["type"]?["backendValue"]?.ToString() ?? "";
                    if (typeSpecificLookup.ContainsKey(itemType))
                    {
                        typeSpecificLookup[itemType][itemId] = item;

                        if (item["name"] != null)
                        {
                            string normalizedName = NormalizeString(item["name"].ToString());
                            typeSpecificLookup[itemType][normalizedName] = item;

                            if (itemType == "AthenaGlider" && itemId.Contains("umbrella"))
                            {
                                typeSpecificLookup[itemType]["umbrella"] = item;
                            }

                            if (itemId.Contains("-"))
                            {
                                string withoutPrefix = itemId.Split(new[] { '-' }, 2)[1];
                                typeSpecificLookup[itemType][withoutPrefix] = item;
                            }
                        }
                    }
                }

                Console.WriteLine($"Optimized dictionaries created in {(DateTime.Now - dictStart).TotalSeconds:F2} seconds");

   
                var exclusiveItems = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "CID_327_Athena_Commando_M_BlueMystery", 
                    "cid_095_athena_commando_m_founder",
                    "cid_096_athena_commando_f_founder",
                    "cid_296_athena_commando_m_math",
                    "cid_360_athena_commando_m_techopsblue",
                    "cid_138_athena_commando_m_psburnout",
                    "cid_114_athena_commando_f_tacticalwoodland",
                    "cid_386_athena_commando_m_streetopsstealth",
                    "cid_441_athena_commando_f_cyberscavengerblue",
                    "cid_a_062_athena_commando_f_alchemy_xd6gp",
                    "cid_964_athena_commando_m_historian_869bc",
                    "cid_971_Athena_Commando_M_Jupiter_S0Z6M",
                    "cid_760_athena_commando_f_neontightsuit",
                    "cid_748_athena_commando_f_hitman",
                    "cid_022_athena_commando_m_galaxy",
                    "cid_295_athena_commando_m_carbideblue",
                    "cid_313_athena_commando_m_venom",
                    "cid_584_athena_commando_m_borderlands",
                    "cid_235_athena_commando_m_bullseye",
                    "cid_803_athena_commando_m_helmet",
                    "cid_694_athena_commando_m_mogul",
                    "cid_013_athena_commando_f",
                    "cid_014_athena_commando_m",
                    "cid_513_athena_commando_f_neoncat",
                    "cid_035_athena_commando_f_psblue",
                    "cid_614_athena_commando_f_tourbus",
                    "cid_236_athena_commando_m_carbidewhite",
                    "cid_350_athena_commando_m_honor",
                    "cid_428_athena_commando_f_historic",
                    "cid_290_athena_commando_m_bomber",
                    "cid_036_athena_commando_m_enforcer",
                    "cid_138_athena_commando_m_carbideblack",
                    "cid_017_athena_commando_m_medieval",
                    "cid_033_athena_commando_m_twitch",
                    "cid_034_athena_commando_f_twitch",
                    "cid_015_athena_commando_m",
                    "cid_677_athena_commando_f_dieselpunk",
                    "cid_703_athena_commando_m_giftbox",
                    // Normalized names for Characters
                    "Cobalt",
                    "Rose team leader",
                    "prodigy",
                    "Carbon Commando",
                    "BlueStriker",
                    "Trailblazer",
                    "StealthReflex",
                    "neoversa",
                    "Aloy",
                    "Kratos",
                    "MasterCheif",
                    "astrojack",
                    "yellowjacket",
                    "galaxy",
                    "eon",
                    "roguespiderknight",
                    "psychobandit",
                    "darkvertex",
                    "rue",
                    "travisscott",
                    "renegaderaider",
                    "aerialassaulttrooper",
                    "glow",
                    "blueteamleader",
                    "wildcat",
                    "doublehelix",
                    "honorguard",
                    "wonder",
                    "royalebomber",
                    "thereaper",
                    "omega",
                    "blackknight",
                    "havoc",
                    "subcommander",
                    "rogueagent",
                    "iris",
                    "ikonik",
                    // Pickaxes
                    "Pickaxe_ID_602_TaxiUpgradedMulticolorFemale",
                    "Pickaxe_ID_029_Assassin",
                    "Pickaxe_ID_013_Teslacoil",
                    "Pickaxe_ID_757_BlizzardBomberFemale1H",
                    "Pickaxe_ID_717_NetworkFemale",
                    "Pickaxe_ID_461_SkullBriteCube",
                    "Pickaxe_ID_398_WildCatFemale",
                    "Pickaxe_ID_195_SpaceBunny",
                    "Pickaxe_ID_560_TacticalWoodlandBlueMale",
                    "Pickaxe_ID_338_BandageNinjaBlue1H",
                    "Pickaxe_ID_400_AquaJacketMale",
                    "Pickaxe_ID_464_LongShortsMale",
                    "Pickaxe_ID_447_SpaceWandererMale",
                    "Pickaxe_ID_256_TechOpsBlue",
                    "Pickaxe_ID_178_SpeedyMidnight",
                    "Pickaxe_ID_116_Celestial",
                    "Pickaxe_ID_077_CarbideWhite",
                    "Pickaxe_ID_088_PSBurnout",
                    "Pickaxe_ID_237_Warpaint",
                    "pickaxe_id_294_candycane",
                    "pickaxe_id_099_modernmilitaryred",
                    "pickaxe_id_039_tacticalblack",
                    "Pickaxe_ID_044_TacticalUrbanHammer",
                    "Pickaxe_ID_153_RoseLeader",
                    // Normalized pickaxe names
                    "Drive Shaft",
                    "Trusty no.2",
                    "AC/DC",
                    "Snowtooth",
                    "Cymitar",
                    "Dark Splitter",
                    "Electri-claw",
                    "Plasma Carrot",
                    "Synaptic Hatchets",
                    "Twin Talons",
                    "Wavecrest",
                    "Perfect Point",
                    "Shooting Starstaff",
                    "Pneumatic Twin",
                    "Dark Razor",
                    "Stellar Axe",
                    "Resonator",
                    "Controller",
                    "Mean Streak",
                    "Merry Mint",
                    "Pinpoint",
                    "Instigator",
                    "Tenderizer",
                    "Rose Glow",
                    // Gliders
                    "Glider_Warthog",
                    "FounderGlider",
                    "FounderUmbrella",
                    "Glider_ID_013_PSBlue",
                    "Glider_ID_090_Celestial",
                    "Glider_ID_018_Twitch",
                    "Glider_ID_067_PSBurnout",
                    "Glider_ID_056_CarbideWhite",
                    "Glider_ID_117_Warpaint",
                    "Glider_ID_137_StreetOpsStealth",
                    "Glider_ID_131_SpeedyMidnight",
                    "Glider_ID_150_TechOpsBlue",
                    "Glider_ID_161_RoseLeader",
                    "Glider_ID_196_CycloneMale",
                    // Normalized glider names
                    "Mako",
                    "FounderGlider",
                    "FounderUmbrella",
                    "Blue Streak",
                    "Discovery",
                    "Slipstream",
                    "Flappy",
                    "Aurora",
                    "Wild Streak",
                    "Stealth Pivot",
                    "Dark Forerunner",
                    "Coaxial Blue",
                    "Rose Rider",
                    "Astro World Cyclone",
                    // Backpacks
                    "bid_027_carbideblue",
                    "bid_002_medieval",
                    "eonshield",
                    "blackshield",
                    // Emotes
                    "eid_tourbus",
                    // Contrails, Music, Wraps
                    "wrap_022_galactic",
                    "Wrap_016_CuddleTeam",
                    "galactic",
                    "Cuddle Hearts"
                };

                var downloader = new ImageDownloader(maxWorkers: 15, cacheDir: "cosmetics_cache");
                var fuzzyMatchCount = new Dictionary<string, int>();

                bool IsExclusiveItem(string itemId, JToken itemData, string category)
                {
                    if (string.IsNullOrEmpty(itemId))
                        return false;

                    if (category == "AthenaCharacter")
                    {
                        string normalizedId = itemId.ToLower();
                        if (normalizedId.Contains("cid_028_athena_commando_f_halloween") || normalizedId.Contains("ghoultrooper"))
                        {
                            if (purchaseDates.TryGetValue(itemId, out DateTime date))
                            {
                                return date >= new DateTime(2017, 10, 26) && date <= new DateTime(2017, 12, 13);
                            }
                            return false;
                        }
                        if (normalizedId.Contains("cid_029_athena_commando_m_halloween") || normalizedId.Contains("skulltrooper"))
                        {
                            if (purchaseDates.TryGetValue(itemId, out DateTime date))
                            {
                                return date >= new DateTime(2017, 10, 26) && date <= new DateTime(2017, 12, 13);
                            }
                            return false;
                        }

                        if (itemData != null)
                        {
                            var shopHistory = itemData["shopHistory"];
                            if (shopHistory == null || !shopHistory.HasValues)
                            {
                                return exclusiveItems.Contains(itemId.ToLower()) || exclusiveItems.Contains(NormalizeString(itemData["name"]?.ToString()));
                            }

                            DateTime cutoff = new DateTime(2022, 7, 11);
                            DateTime lastAppearance = DateTime.MinValue;
                            foreach (var date in shopHistory)
                            {
                                if (DateTime.TryParse(date.ToString(), out DateTime shopDate) && shopDate > lastAppearance)
                                {
                                    lastAppearance = shopDate;
                                }
                            }

                            if (lastAppearance != DateTime.MinValue && lastAppearance <= cutoff)
                            {
                                return true;
                            }
                        }
                    }

                    if (exclusiveItems.Contains(itemId.ToLower()))
                        return true;

                    string normalizedName = NormalizeString(itemId);
                    if (exclusiveItems.Contains(normalizedName))
                        return true;

                    if (itemData != null)
                    {
                        string dataId = itemData["id"]?.ToString()?.ToLower();
                        if (!string.IsNullOrEmpty(dataId) && exclusiveItems.Contains(dataId))
                            return true;

                        string dataName = itemData["name"]?.ToString();
                        if (!string.IsNullOrEmpty(dataName) && exclusiveItems.Contains(NormalizeString(dataName)))
                            return true;
                    }

                    return false;
                }

                JToken FindItemMatch(string itemId, string category)
                {
                    itemId = itemId.Trim().ToLower();

                    if (exclusiveItems.Contains(itemId) && typeSpecificLookup[category].ContainsKey(itemId))
                        return typeSpecificLookup[category][itemId];

                    if (cosmeticsLookup.ContainsKey(itemId))
                    {
                        var item = cosmeticsLookup[itemId];
                        if (item["type"]?["backendValue"]?.ToString() == category)
                            return item;
                    }

                    if (typeSpecificLookup[category].ContainsKey(itemId))
                        return typeSpecificLookup[category][itemId];

                    if (category == "AthenaBackpack" && itemId.Contains("petcarrier-"))
                    {
                        foreach (var kvp in typeSpecificLookup[category])
                        {
                            if (kvp.Key.Contains("petcarrier-") && kvp.Key.Split("petcarrier-")[1].Contains(itemId))
                                return kvp.Value;
                            if (kvp.Key.Contains("petcarrier-") && itemId.Replace("petcarrier-", "").Contains(kvp.Key))
                                return kvp.Value;
                        }
                    }

                    if (itemId.Contains("-"))
                    {
                        string withoutPrefix = itemId.Split(new[] { '-' }, 2)[1];
                        if (typeSpecificLookup[category].ContainsKey(withoutPrefix))
                            return typeSpecificLookup[category][withoutPrefix];
                    }

                    string variation = itemId.Replace("-", "_");
                    if (typeSpecificLookup[category].ContainsKey(variation))
                        return typeSpecificLookup[category][variation];

                    if (category == "AthenaGlider" && itemId.Contains("umbrella"))
                    {
                        if (typeSpecificLookup[category].ContainsKey("umbrella"))
                            return typeSpecificLookup[category]["umbrella"];
                    }

                    string normalizedId = NormalizeString(itemId);

                    if (fuzzyMatchCount.GetValueOrDefault(category, 0) < 10)
                    {
                        fuzzyMatchCount[category] = fuzzyMatchCount.GetValueOrDefault(category, 0) + 1;

                        JToken bestMatch = null;
                        double bestScore = 0.65;

                        foreach (var kvp in typeSpecificLookup[category])
                        {
                            if (kvp.Key.Length < 3)
                                continue;

                            int matches = kvp.Key.Zip(normalizedId, (c1, c2) => c1 == c2 ? 1 : 0).Sum();
                            double similarity = (double)matches / Math.Max(kvp.Key.Length, normalizedId.Length);

                            if (similarity > bestScore)
                            {
                                bestScore = similarity;
                                bestMatch = kvp.Value;
                            }
                        }

                        return bestMatch;
                    }

                    return null;
                }

                async Task<(string Category, int Found, int NotFound, double Time, List<(string ItemId, JToken ItemData, bool IsExclusive, Image Image, bool Found)> Items)> ProcessCategoryAsync(string category)
                {
                    var categoryStart = DateTime.Now;
                    Console.WriteLine($"\nStarting category: {category}");

                    var subfolder = Path.Combine(ResultsFolder, Categories[category].Subfolder);
                    string filePath = Path.Combine(subfolder, Categories[category].TextFile);
                    if (!File.Exists(filePath))
                    {
                        Console.WriteLine($"File not found: {filePath}");
                        return (category, 0, 0, 0, new List<(string, JToken, bool, Image, bool)>());
                    }

                    var itemIds = (await File.ReadAllLinesAsync(filePath)).Select(line => line.Trim()).Where(line => !string.IsNullOrEmpty(line)).ToList();

                    if (!itemIds.Any())
                    {
                        Console.WriteLine($"No items found in {filePath}");
                        return (category, 0, 0, 0, new List<(string, JToken, bool, Image, bool)>());
                    }

                    Console.WriteLine($"Category: {category}, Items found: {itemIds.Count}");

                    var rarityRank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                    {
                        { "mythic", 6 },
                        { "legendary", 5 },
                        { "epic", 4 },
                        { "rare", 3 },
                        { "uncommon", 2 },
                        { "common", 1 },
                        { "marvel", 0 },
                        { "dc", 0 },
                        { "icon", 0 },
                        { "starwars", 0 }
                    };

                    var itemsWithData = new List<(string ItemId, JToken ItemData, bool IsExclusive)>();
                    foreach (var itemId in itemIds)
                    {
                        var itemData = FindItemMatch(itemId, category);
                        bool isExclusive = IsExclusiveItem(itemId, itemData, category);
                        itemsWithData.Add((itemId, itemData, isExclusive));
                    }

                    var sortedItems = itemsWithData
                        .OrderByDescending(item =>
                        {
                            if (item.IsExclusive)
                                return rarityRank["mythic"];
                            string rarity = item.ItemData?["rarity"]?["value"]?.ToString()?.ToLower() ?? "common";
                            return rarityRank.ContainsKey(rarity) ? rarityRank[rarity] : 0;
                        })
                        .ThenBy(item => item.ItemData?["name"]?.ToString() ?? item.ItemId)
                        .ToList();

                    int itemCount = sortedItems.Count;
                    int cols = (int)Math.Ceiling(Math.Sqrt(itemCount * 1.5));
                    int rows = (int)Math.Ceiling((double)itemCount / cols);

                    int thumbnailSize = Math.Max(80, Math.Min(200, 1200 / cols));
                    int horizontalSpacing = 0;
                    int verticalSpacing = Math.Max(40, thumbnailSize / 2);
                    int textHeight = Math.Max(16, thumbnailSize / 6);
                    int padding = 5;

                    int margin = 60;
                    int canvasWidth = margin * 2 + cols * (thumbnailSize + horizontalSpacing);
                    int canvasHeight = margin * 2 + rows * (thumbnailSize + verticalSpacing + textHeight);

                    using var lockerImage = new Bitmap(canvasWidth, canvasHeight);
                    using var graphics = Graphics.FromImage(lockerImage);
                    graphics.Clear(Color.FromArgb(25, 25, 35));

                    var baseFontSize = Math.Max(12, thumbnailSize / 8);
                    var titleFontSize = Math.Max(24, (int)(baseFontSize * 1.8));
                    var titleFont = new Font("Arial", titleFontSize);

                    string categoryName = category.Replace("Athena", "");
                    string leftText = "discord.gg/fuckniggas";
                    string centerText = $"{categoryName} ({itemCount} ITEMS)";

                    var centerTextSize = graphics.MeasureString(centerText, titleFont);

                    graphics.DrawString(leftText, titleFont, Brushes.White, margin, margin / 2);
                    graphics.DrawString(centerText, titleFont, Brushes.White, canvasWidth / 2 - centerTextSize.Width / 2, margin / 2);

                    int foundCount = 0;
                    int notFoundCount = 0;

                    var results = new ConcurrentBag<(int Index, int X, int Y, string ItemId, bool Found, Image Image, JToken ItemData, bool IsExclusive)>();
                    var processedItems = new List<(string ItemId, JToken ItemData, bool IsExclusive, Image Image, bool Found)>();

                    async Task ProcessItemAsync(int i, (string ItemId, JToken ItemData, bool IsExclusive) item)
                    {
                        int row = i / cols;
                        int col = i % cols;

                        int x = margin + col * (thumbnailSize + horizontalSpacing);
                        int y = margin + row * (thumbnailSize + verticalSpacing + textHeight) + 60;

                        var result = (Index: i, X: x, Y: y, ItemId: item.ItemId, Found: false, Image: (Image)null, ItemData: item.ItemData, IsExclusive: item.IsExclusive);

                        try
                        {
                            if (item.ItemData?["images"] != null)
                            {
                                result.Found = true;

                                string iconUrl = null;
                                foreach (var imgKey in new[] { "icon", "smallIcon", "featured" })
                                {
                                    if (item.ItemData["images"][imgKey]?.ToString() != null)
                                    {
                                        iconUrl = item.ItemData["images"][imgKey].ToString();
                                        break;
                                    }
                                }

                                if (iconUrl == null && item.ItemData["images"]["lego"] != null)
                                {
                                    foreach (var legoKey in new[] { "large", "small" })
                                    {
                                        if (item.ItemData["images"]["lego"][legoKey]?.ToString() != null)
                                        {
                                            iconUrl = item.ItemData["images"]["lego"][legoKey].ToString();
                                            break;
                                        }
                                    }
                                }

                                if (iconUrl != null)
                                {
                                    var image = await downloader.DownloadImageAsync(iconUrl, new Size(thumbnailSize, thumbnailSize));
                                    if (image != null)
                                    {
                                        result.Image = image;
                                    }
                                }
                            }
                        }
                        catch (Exception e)
                        {
                            Console.WriteLine($"Error processing {item.ItemId}: {e.Message}");
                        }

                        results.Add(result);
                        processedItems.Add((item.ItemId, item.ItemData, item.IsExclusive, result.Image, result.Found));
                    }

                    await Task.WhenAll(sortedItems.Select((item, i) => ProcessItemAsync(i, item)));

                    foreach (var result in results.OrderBy(r => r.Index))
                    {
                        int x = result.X;
                        int y = result.Y;
                        string itemId = result.ItemId;

                        if (result.Found && result.Image != null)
                        {
                            foundCount++;

                            var itemData = result.ItemData;
                            string rarity = result.IsExclusive ? "mythic" : (itemData["rarity"]?["value"]?.ToString()?.ToLower() ?? "common");
                            var bgColors = new Dictionary<string, Color>
                            {
                                { "common", Color.FromArgb(150, 150, 150) },
                                { "uncommon", Color.FromArgb(96, 170, 58) },
                                { "rare", Color.FromArgb(73, 172, 242) },
                                { "epic", Color.FromArgb(177, 91, 226) },
                                { "legendary", Color.FromArgb(211, 120, 65) },
                                { "mythic", Color.FromArgb(235, 227, 88) },
                                { "marvel", Color.FromArgb(197, 51, 52) },
                                { "dc", Color.FromArgb(84, 117, 199) },
                                { "icon", Color.FromArgb(63, 181, 181) },
                                { "starwars", Color.FromArgb(32, 85, 128) }
                            };
                            var bgColor = bgColors.ContainsKey(rarity) ? bgColors[rarity] : Color.FromArgb(100, 100, 100);

                            graphics.FillRectangle(Brushes.Black, x - 2, y - 2, thumbnailSize + 4, thumbnailSize + 4);
                            graphics.FillRectangle(new SolidBrush(bgColor), x, y, thumbnailSize, thumbnailSize);
                            graphics.DrawImage(result.Image, x, y, thumbnailSize, thumbnailSize);

                            string nameToDisplay = itemData["name"]?.ToString() ?? itemId;
                            int maxLength = (int)(thumbnailSize / baseFontSize * 2);
                            if (nameToDisplay.Length > maxLength)
                            {
                                nameToDisplay = nameToDisplay.Substring(0, maxLength - 3) + "...";
                            }

                            float fontSize = baseFontSize;
                            var font = new Font("Arial", fontSize);
                            var textSize = graphics.MeasureString(nameToDisplay, font);
                            while (textSize.Width > thumbnailSize && fontSize > 8)
                            {
                                fontSize -= 1;
                                font.Dispose();
                                font = new Font("Arial", fontSize);
                                textSize = graphics.MeasureString(nameToDisplay, font);
                            }

                            int textY = y + thumbnailSize + 8;
                            var bgRect = new RectangleF(
                                x,
                                textY - padding,
                                thumbnailSize,
                                fontSize + 2 * padding
                            );

                            graphics.FillRectangle(new SolidBrush(Color.FromArgb(180, 0, 0, 0)), bgRect);
                            graphics.DrawString(nameToDisplay, font, Brushes.Black, x + thumbnailSize / 2 + 1, textY + 1, new StringFormat { Alignment = StringAlignment.Center });
                            graphics.DrawString(nameToDisplay, font, Brushes.White, x + thumbnailSize / 2, textY, new StringFormat { Alignment = StringAlignment.Center });
                            font.Dispose();
                        }
                        else
                        {
                            notFoundCount++;
                            graphics.FillRectangle(Brushes.DarkGray, x, y, thumbnailSize, thumbnailSize);
                            graphics.FillRectangle(new SolidBrush(Color.FromArgb(40, 40, 40)), x + 3, y + 3, thumbnailSize - 6, thumbnailSize - 6);

                            string displayName = itemId;
                            int maxLength = (int)(thumbnailSize / baseFontSize * 2);
                            if (displayName.Length > maxLength)
                            {
                                displayName = displayName.Substring(0, maxLength - 3) + "...";
                            }

                            float fontSize = baseFontSize;
                            var font = new Font("Arial", fontSize);
                            var textSize = graphics.MeasureString(displayName, font);
                            while (textSize.Width > thumbnailSize && fontSize > 8)
                            {
                                fontSize -= 1;
                                font.Dispose();
                                font = new Font("Arial", fontSize);
                                textSize = graphics.MeasureString(displayName, font);
                            }

                            graphics.DrawString("?", titleFont, Brushes.Gray, x + thumbnailSize / 2, y + thumbnailSize / 2 - 10, new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });
                            graphics.DrawString(displayName, font, Brushes.Gray, x + thumbnailSize / 2, y + thumbnailSize / 2 + 15, new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });

                            int textY = y + thumbnailSize + 8;
                            graphics.FillRectangle(new SolidBrush(Color.FromArgb(100, 30, 30)), x, textY, thumbnailSize, fontSize + 6);
                            graphics.DrawString("Not Found", font, Brushes.Pink, x + thumbnailSize / 2, textY + 3, new StringFormat { Alignment = StringAlignment.Center });
                            font.Dispose();
                        }
                    }

                    string filename = Path.Combine(subfolder, Categories[category].ImageFile);
                    lockerImage.Save(filename, ImageFormat.Png);

                    double categoryTime = (DateTime.Now - categoryStart).TotalSeconds;
                    Console.WriteLine($"Created image for {category}: [{foundCount}] items found, [{notFoundCount}] not found");
                    Console.WriteLine($"Total time for {category}: [{categoryTime:F2}]s");

                    return (category, foundCount, notFoundCount, categoryTime, processedItems);
                }

                var results = new List<(string Category, int Found, int NotFound, double Time, List<(string ItemId, JToken ItemData, bool IsExclusive, Image Image, bool Found)> Items)>();
                var tasks = Categories.Keys.Select(category => ProcessCategoryAsync(category)).ToList();
                results.AddRange(await Task.WhenAll(tasks));

                Console.WriteLine("\nGenerating combined cosmetics image...");
                var combinedStart = DateTime.Now;

                var allItems = new List<(string ItemId, JToken ItemData, bool IsExclusive, Image Image, bool Found)>();
                int totalFound = 0;
                int totalNotFound = 0;

                foreach (var result in results)
                {
                    allItems.AddRange(result.Items);
                    totalFound += result.Found;
                    totalNotFound += result.NotFound;
                }

                var rarityRank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                {
                    { "mythic", 6 },
                    { "legendary", 5 },
                    { "epic", 4 },
                    { "rare", 3 },
                    { "uncommon", 2 },
                    { "common", 1 },
                    { "marvel", 0 },
                    { "dc", 0 },
                    { "icon", 0 },
                    { "starwars", 0 }
                };

                var sortedItems = allItems
                    .OrderByDescending(item =>
                    {
                        if (item.IsExclusive)
                            return rarityRank["mythic"];
                        string rarity = item.ItemData?["rarity"]?["value"]?.ToString()?.ToLower() ?? "common";
                        return rarityRank.ContainsKey(rarity) ? rarityRank[rarity] : 0;
                    })
                    .ThenBy(item => item.ItemData?["name"]?.ToString() ?? item.ItemId)
                    .ToList();

                int itemCount = sortedItems.Count;
                if (itemCount == 0)
                {
                    Console.WriteLine("No items to display in combined image.");
                    return;
                }

                int cols = (int)Math.Ceiling(Math.Sqrt(itemCount * 1.5));
                int rows = (int)Math.Ceiling((double)itemCount / cols);

                int thumbnailSize = Math.Max(80, Math.Min(200, 1200 / cols));
                int horizontalSpacing = 0;
                int verticalSpacing = Math.Max(40, thumbnailSize / 2);
                int textHeight = Math.Max(16, thumbnailSize / 6);
                int padding = 5;

                int margin = 60;
                int canvasWidth = margin * 2 + cols * (thumbnailSize + horizontalSpacing);
                int canvasHeight = margin * 2 + rows * (thumbnailSize + verticalSpacing + textHeight);

                using var combinedImage = new Bitmap(canvasWidth, canvasHeight);
                using var combinedGraphics = Graphics.FromImage(combinedImage);
                combinedGraphics.Clear(Color.FromArgb(25, 25, 35));

                var baseFontSize = Math.Max(12, thumbnailSize / 8);
                var titleFontSize = Math.Max(24, (int)(baseFontSize * 1.8));
                var titleFont = new Font("Arial", titleFontSize);

                string leftText = "discord.gg/fuckniggas";
                string centerText = $"All Cosmetics ({itemCount} Items)";

                var centerSize = combinedGraphics.MeasureString(centerText, titleFont);

                combinedGraphics.DrawString(leftText, titleFont, Brushes.White, margin, margin / 2);
                combinedGraphics.DrawString(centerText, titleFont, Brushes.White, canvasWidth / 2 - centerSize.Width / 2, margin / 2);

                for (int i = 0; i < sortedItems.Count; i++)
                {
                    var item = sortedItems[i];
                    int row = i / cols;
                    int col = i % cols;

                    int x = margin + col * (thumbnailSize + horizontalSpacing);
                    int y = margin + row * (thumbnailSize + verticalSpacing + textHeight) + 60;

                    if (item.Found && item.Image != null)
                    {
                        var itemData = item.ItemData;
                        string rarity = item.IsExclusive ? "mythic" : (itemData["rarity"]?["value"]?.ToString()?.ToLower() ?? "common");
                        var bgColors = new Dictionary<string, Color>
                        {
                            { "common", Color.FromArgb(150, 150, 150) },
                            { "uncommon", Color.FromArgb(96, 170, 58) },
                            { "rare", Color.FromArgb(73, 172, 242) },
                            { "epic", Color.FromArgb(177, 91, 226) },
                            { "legendary", Color.FromArgb(211, 120, 65) },
                            { "mythic", Color.FromArgb(235, 227, 88) },
                            { "marvel", Color.FromArgb(197, 51, 52) },
                            { "dc", Color.FromArgb(84, 117, 199) },
                            { "icon", Color.FromArgb(63, 181, 181) },
                            { "starwars", Color.FromArgb(32, 85, 128) }
                        };
                        var bgColor = bgColors.ContainsKey(rarity) ? bgColors[rarity] : Color.FromArgb(100, 100, 100);

                        combinedGraphics.FillRectangle(Brushes.Black, x - 2, y - 2, thumbnailSize + 4, thumbnailSize + 4);
                        combinedGraphics.FillRectangle(new SolidBrush(bgColor), x, y, thumbnailSize, thumbnailSize);
                        combinedGraphics.DrawImage(item.Image, x, y, thumbnailSize, thumbnailSize);

                        string nameToDisplay = itemData["name"]?.ToString() ?? item.ItemId;
                        int maxLength = (int)(thumbnailSize / baseFontSize * 2);
                        if (nameToDisplay.Length > maxLength)
                        {
                            nameToDisplay = nameToDisplay.Substring(0, maxLength - 3) + "...";
                        }

                        float fontSize = baseFontSize;
                        var font = new Font("Arial", fontSize);
                        var textSize = combinedGraphics.MeasureString(nameToDisplay, font);
                        while (textSize.Width > thumbnailSize && fontSize > 8)
                        {
                            fontSize -= 1;
                            font.Dispose();
                            font = new Font("Arial", fontSize);
                            textSize = combinedGraphics.MeasureString(nameToDisplay, font);
                        }

                        int textY = y + thumbnailSize + 8;
                        var bgRect = new RectangleF(
                            x,
                            textY - padding,
                            thumbnailSize,
                            fontSize + 2 * padding
                        );

                        combinedGraphics.FillRectangle(new SolidBrush(Color.FromArgb(180, 0, 0, 0)), bgRect);
                        combinedGraphics.DrawString(nameToDisplay, font, Brushes.Black, x + thumbnailSize / 2 + 1, textY + 1, new StringFormat { Alignment = StringAlignment.Center });
                        combinedGraphics.DrawString(nameToDisplay, font, Brushes.White, x + thumbnailSize / 2, textY, new StringFormat { Alignment = StringAlignment.Center });
                        font.Dispose();
                    }
                    else
                    {
                        combinedGraphics.FillRectangle(Brushes.DarkGray, x, y, thumbnailSize, thumbnailSize);
                        combinedGraphics.FillRectangle(new SolidBrush(Color.FromArgb(40, 40, 40)), x + 3, y + 3, thumbnailSize - 6, thumbnailSize - 6);

                        string displayName = item.ItemId;
                        int maxLength = (int)(thumbnailSize / baseFontSize * 2);
                        if (displayName.Length > maxLength)
                        {
                            displayName = displayName.Substring(0, maxLength - 3) + "...";
                        }

                        float fontSize = baseFontSize;
                        var font = new Font("Arial", fontSize);
                        var textSize = combinedGraphics.MeasureString(displayName, font);
                        while (textSize.Width > thumbnailSize && fontSize > 8)
                        {
                            fontSize -= 1;
                            font.Dispose();
                            font = new Font("Arial", fontSize);
                            textSize = combinedGraphics.MeasureString(displayName, font);
                        }

                        combinedGraphics.DrawString("?", titleFont, Brushes.Gray, x + thumbnailSize / 2, y + thumbnailSize / 2 - 10, new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });
                        combinedGraphics.DrawString(displayName, font, Brushes.Gray, x + thumbnailSize / 2, y + thumbnailSize / 2 + 15, new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });

                        int textY = y + thumbnailSize + 8;
                        combinedGraphics.FillRectangle(new SolidBrush(Color.FromArgb(100, 30, 30)), x, textY, thumbnailSize, fontSize + 6);
                        combinedGraphics.DrawString("Not Found", font, Brushes.Pink, x + thumbnailSize / 2, textY + 3, new StringFormat { Alignment = StringAlignment.Center });
                        font.Dispose();
                    }
                }

                string combinedFilename = Path.Combine(ResultsFolder, "AllCosmetics.png");
                combinedImage.Save(combinedFilename, ImageFormat.Png);
                Console.WriteLine($"Created combined cosmetics image: {combinedFilename} with [{totalFound}] items found, [{totalNotFound}] not found");
                Console.WriteLine($"Total time for combined image: [{(DateTime.Now - combinedStart).TotalSeconds:F2}]s");

                double totalTime = (DateTime.Now - startTime).TotalSeconds;
                Console.WriteLine($"\nTotal execution time: [{totalTime:F2}] seconds");
                Console.WriteLine("Summary:");
                foreach (var result in results.OrderBy(r => r.Category))
                {
                    Console.Clear();
                    Console.WriteLine("Summary (press enter to see next page):");
                    Console.WriteLine($"  {result.Category}: [{result.Found}] found, [{result.NotFound}] not found, [{result.Time:F2}s]");
                    Console.WriteLine("Saved images in Skin Results folder.");
                    Console.ReadLine();
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"Error in create_locker_image: {e.Message}");
                Console.WriteLine(e.StackTrace);
            }
        }

        static async Task Main(string[] args)
        {
            if (!Directory.Exists(ResultsFolder))
            {
                Directory.CreateDirectory(ResultsFolder);
            }

            foreach (var category in Categories.Values)
            {
                var subfolder = Path.Combine(ResultsFolder, category.Subfolder);
                if (!Directory.Exists(subfolder))
                {
                    Directory.CreateDirectory(subfolder);
                }
            }
            Console.Title = "[discord.gg/fuckniggas] - made with <3 by vows";
            Console.WriteLine("[discord.gg/fuckniggas] - made with <3 by vows");
            Console.WriteLine("");

            while (true)
            {
                Console.WriteLine("Select an option:");
                Console.WriteLine("(1) Skin Check");
                Console.WriteLine("(2) Extract Info From PDF");
                Console.Write("Enter your choice (1 or 2): ");
                string choice = Console.ReadLine()?.Trim();

                if (choice != "1" && choice != "2")
                {
                    Console.WriteLine("Invalid choice. Please enter 1 or 2.");
                    continue;
                }

                string filePath = null;
                while (true)
                {
                    Console.Write("Please enter PDF file path : ");
                    filePath = Console.ReadLine()?.Trim();

                    if (string.IsNullOrEmpty(filePath))
                    {
                        Console.WriteLine("No file path provided. Exiting program.");
                        return;
                    }

                    if (!filePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                    {
                        Console.WriteLine("Error: Must be a pdf file.");
                        continue;
                    }

                    if (!Path.IsPathRooted(filePath))
                    {
                        filePath = Path.Combine(Directory.GetCurrentDirectory(), filePath);
                    }

                    if (!File.Exists(filePath))
                    {
                        Console.WriteLine($"Error: The file path does not exist.");
                        continue;
                    }

                    break;
                }

                string password = null;
                bool passwordRequired = false;

                try
                {
                    using var reader = new PdfReader(filePath);
                    using var pdfDoc = new PdfDocument(reader);
                }
                catch (iText.Kernel.Exceptions.BadPasswordException)
                {
                    passwordRequired = true;
                }
                catch (iText.Kernel.Exceptions.PdfException e)
                {
                    Console.WriteLine($"Error: The file is not a valid PDF: {e.Message}. Exiting program.");
                    return;
                }
                catch (Exception e)
                {
                    Console.WriteLine($"Error accessing PDF: {e.Message}. Exiting program.");
                    return;
                }

                if (passwordRequired)
                {
                    Console.WriteLine("Check the email from Epic Games for the password.");
                    Console.Write("Enter PDF password : ");
                    password = Console.ReadLine()?.Trim();

                    if (string.IsNullOrEmpty(password))
                    {
                        Console.WriteLine("No password provided. Exiting program.");
                        return;
                    }

                    try
                    {
                        using var reader = new PdfReader(filePath, new ReaderProperties().SetPassword(System.Text.Encoding.UTF8.GetBytes(password)));
                        using var pdfDoc = new PdfDocument(reader);
                        Console.WriteLine("Password accepted!");
                    }
                    catch (iText.Kernel.Exceptions.BadPasswordException)
                    {
                        Console.WriteLine("Incorrect password. Exiting program.");
                        return;
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine($"Error processing PDF: {e.Message}. Exiting program.");
                        return;
                    }
                }

                if (choice == "1")
                {
                    Console.WriteLine("\nExtracting data from PDF...");
                    var (items, purchaseDates, categoryCounts) = await ProcessPdfAsync(filePath, password);
                    if (items == null || purchaseDates == null || categoryCounts == null)
                    {
                        Console.WriteLine("Failed to process PDF. Exiting program.");
                        return;
                    }

                    Console.WriteLine("\nChecking for cosmetics data...");
                    if (await EnsureCosmeticsDataAsync())
                    {
                        Console.WriteLine("\nGenerating locker images...");
                        await CreateLockerImageAsync(purchaseDates);
                        Console.WriteLine("\nPDF extracted, images compiled.");
                    }
                    else
                    {
                        Console.WriteLine("Failed to obtain cosmetics data. Cannot create locker images.");
                    }

 
                    Console.WriteLine("\nExtracting info to text file...");
                    await ExtractInfoToTextFileAsync(filePath, password, categoryCounts);
                }
                else if (choice == "2")
                {
                    Console.WriteLine("\nExtracting data from PDF...");
                    var (items, purchaseDates, categoryCounts) = await ProcessPdfAsync(filePath, password);
                    if (items == null || purchaseDates == null || categoryCounts == null)
                    {
                        Console.WriteLine("Failed to process PDF. Exiting program.");
                        return;
                    }
                    await ExtractInfoToTextFileAsync(filePath, password, categoryCounts);
                    Console.WriteLine("\nData extraction complete.");
                }

                break;
            }
        }
    }
}
