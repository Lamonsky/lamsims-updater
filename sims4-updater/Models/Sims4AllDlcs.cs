using sims4_updater.Helpers;
using System.Collections.ObjectModel;

namespace sims4_updater.Models
{
    public class Sims4AllDlcs
    {
        public ObservableCollection<Sims4DLC> Sims4DLCs { get; set; } = new ObservableCollection<Sims4DLC>();
        public ObservableCollection<Sims4DLC> OriginalSims4DLCs { get; set; } = new ObservableCollection<Sims4DLC>();

        public Sims4AllDlcs()
        {
            //Rozszerzenia (EP)
            Sims4DLCs.Add(new Sims4DLC { Code = "EP01", Name = "The Sims 4 Get to Work", Url = "https://gofile.io/d/pcJSej", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "EP02", Name = "The Sims 4 Get Together", Url = "https://gofile.io/d/6RRxJe", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "EP03", Name = "The Sims 4 City Living", Url = "https://gofile.io/d/bs9Cd1", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "EP04", Name = "The Sims 4 Cats & Dogs", Url = "https://gofile.io/d/g6P3I3", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "EP05", Name = "The Sims 4 Seasons", Url = "https://gofile.io/d/ANmgtz", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "EP06", Name = "The Sims 4 Get Famous", Url = "https://gofile.io/d/nzLOw0", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "EP07", Name = "The Sims 4 Island Living", Url = "https://gofile.io/d/37NvGf", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "EP08", Name = "The Sims 4 Discover University", Url = "https://gofile.io/d/nx58OM", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "EP09", Name = "The Sims 4 Eco Lifestyle", Url = "https://gofile.io/d/qarGcZ", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "EP10", Name = "The Sims 4 Snowy Escape", Url = "https://gofile.io/d/s6rjEA", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "EP11", Name = "The Sims 4 Cottage Living", Url = "https://gofile.io/d/0FCARK", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "EP12", Name = "The Sims 4 High School Years", Url = "https://gofile.io/d/rOuop3", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "EP13", Name = "The Sims 4 Growing Together", Url = "https://gofile.io/d/hCFrmO", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "EP14", Name = "The Sims 4 Horse Ranch", Url = "https://gofile.io/d/uPjIsR", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "EP15", Name = "The Sims 4 For Rent", Url = "https://gofile.io/d/sFvU8z", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "EP16", Name = "The Sims 4 Lovestruck", Url = "https://gofile.io/d/fiuaUa", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "EP17", Name = "The Sims 4 Life & Death", Url = "https://gofile.io/d/a5VDFL", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "EP18", Name = "The Sims 4 Businesses & Hobbies", Url = "https://gofile.io/d/Oj8tF6", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "EP19", Name = "The Sims 4 Enchanted by Natures", Url = "https://gofile.io/d/tnFZgG", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "EP20", Name = "The Sims 4 Adventure Awaits", Url = "https://gofile.io/d/UO3hQb", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "EP21", Name = "The Sims 4 Royalty & Legacy", Url = "https://gofile.io/d/K6qNZ1", Installed = false });
            //FP
            Sims4DLCs.Add(new Sims4DLC { Code = "FP01", Name = "The Sims 4 Holiday Celebration", Url = "https://gofile.io/d/JQ9Qxp", Installed = false});
            //Pakiety (GP)
            Sims4DLCs.Add(new Sims4DLC { Code = "GP01", Name = "The Sims 4 Outdoor Retreat", Url = "https://gofile.io/d/8OWjiv", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "GP02", Name = "The Sims 4 Spa Day", Url = "https://gofile.io/d/RAQgPv", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "GP03", Name = "The Sims 4 Dine Out", Url = "https://gofile.io/d/dw0OL0", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "GP04", Name = "The Sims 4 Vampires", Url = "https://gofile.io/d/85OFpy", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "GP05", Name = "The Sims 4 Parenthood", Url = "https://gofile.io/d/wbgDPo", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "GP06", Name = "The Sims 4 Jungle Adventure", Url = "https://gofile.io/d/UyuJuD", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "GP07", Name = "The Sims 4 StrangerVille", Url = "https://gofile.io/d/hctGvM", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "GP08", Name = "The Sims 4 Realm of Magic", Url = "https://gofile.io/d/ArNGoM", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "GP09", Name = "The Sims 4 Star Wars: Journey to Batuu", Url = "https://gofile.io/d/yDyCBY", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "GP10", Name = "The Sims 4 Dream Home Decorator", Url = "https://gofile.io/d/g4mxt8", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "GP11", Name = "The Sims 4 My Wedding Stories", Url = "https://gofile.io/d/lNwopH", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "GP12", Name = "The Sims 4 Werewolves", Url = "https://gofile.io/d/fMw9AI", Installed = false });
            //Akcesoria (SP)
            Sims4DLCs.Add(new Sims4DLC { Code = "SP01", Name = "The Sims 4 Luxury Party Stuff", Url = "https://gofile.io/d/j9nO5z", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP02", Name = "The Sims 4 Perfect Patio Stuff", Url = "https://gofile.io/d/MPermC", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP03", Name = "The Sims 4 Cool Kitchen Stuff", Url = "https://gofile.io/d/OnsvLE", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP04", Name = "The Sims 4 Spooky Stuff", Url = "https://gofile.io/d/9fiqRL", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP05", Name = "The Sims 4 Movie Hangout Stuff", Url = "https://gofile.io/d/9686dT", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP06", Name = "The Sims 4 Romantic Garden Stuff", Url = "https://gofile.io/d/O0xC4z", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP07", Name = "The Sims 4 Kids Room Stuff", Url = "https://gofile.io/d/y7jL5y", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP08", Name = "The Sims 4 Backyard Stuff", Url = "https://gofile.io/d/vVjsJW", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP09", Name = "The Sims 4 Vintage Glamour Stuff", Url = "https://gofile.io/d/oZJrVD", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP10", Name = "The Sims 4 Bowling Night Stuff", Url = "https://gofile.io/d/uTgkHd", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP11", Name = "The Sims 4 Fitness Stuff", Url = "https://gofile.io/d/hU7O3d", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP12", Name = "The Sims 4 Toddler Stuff", Url = "https://gofile.io/d/x3Jrv4", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP13", Name = "The Sims 4 Laundry Day Stuff", Url = "https://gofile.io/d/ehIxjJ", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP14", Name = "The Sims 4 My First Pet Stuff", Url = "https://gofile.io/d/Gyq9YK", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP15", Name = "The Sims 4 Moschino Stuff", Url = "https://gofile.io/d/yWzyho", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP16", Name = "The Sims 4 Tiny Living Stuff", Url = "https://gofile.io/d/xqhYR8", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP17", Name = "The Sims 4 Nifty Knitting Stuff", Url = "https://gofile.io/d/tzsGa3", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP18", Name = "The Sims 4 Paranormal Stuff", Url = "https://gofile.io/d/y5cYmF", Installed = false });
            // Kolekcje (Kits)
            Sims4DLCs.Add(new Sims4DLC { Code = "SP20", Name = "The Sims 4 Throwback Fit Kit", Url = "https://gofile.io/d/VqEObZ", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP21", Name = "The Sims 4 Country Kitchen Kit", Url = "https://gofile.io/d/p3MHsP", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP22", Name = "The Sims 4 Bust the Dust Kit", Url = "https://gofile.io/d/XMdFTk", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP23", Name = "The Sims 4 Courtyard Oasis Kit", Url = "https://gofile.io/d/SMMCil", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP24", Name = "The Sims 4 Fashion Street Kit", Url = "https://gofile.io/d/6Hyprq", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP25", Name = "The Sims 4 Industrial Loft Kit", Url = "https://gofile.io/d/m6Rlnq", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP26", Name = "The Sims 4 Incheon Arrivals Kit", Url = "https://gofile.io/d/WY7noy", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP28", Name = "The Sims 4 Modern Menswear Kit", Url = "https://gofile.io/d/AKdHb8", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP29", Name = "The Sims 4 Blooming Rooms Kit", Url = "https://gofile.io/d/6eC8GB", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP30", Name = "The Sims 4 Carnaval Streetwear Kit", Url = "https://gofile.io/d/R0fwuz", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP31", Name = "The Sims 4 Décor to the Max Kit", Url = "https://gofile.io/d/UuUYIg", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP32", Name = "The Sims 4 Moonlight Chic Kit", Url = "https://gofile.io/d/sibrOX", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP33", Name = "The Sims 4 Little Campers Kit", Url = "https://gofile.io/d/gSndAF", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP34", Name = "The Sims 4 First Fits Kit", Url = "https://gofile.io/d/XHgmSi", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP35", Name = "The Sims 4 Desert Luxe Kit", Url = "https://gofile.io/d/ZLY1DV", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP36", Name = "The Sims 4 Pastel Pop Kit", Url = "https://gofile.io/d/P1yusQ", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP37", Name = "The Sims 4 Everyday Clutter Kit", Url = "https://gofile.io/d/Z4ao1x", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP38", Name = "The Sims 4 Simtimates Collection Kit", Url = "https://gofile.io/d/YWAKWh", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP39", Name = "The Sims 4 Bathroom Clutter Kit", Url = "https://gofile.io/d/eLZInr", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP40", Name = "The Sims 4 Greenhouse Haven Kit", Url = "https://gofile.io/d/0mBWvf", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP41", Name = "The Sims 4 Basement Treasures Kit", Url = "https://gofile.io/d/q5zt7w", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP42", Name = "The Sims 4 Grunge Revival Kit", Url = "https://gofile.io/d/hSSGCR", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP43", Name = "The Sims 4 Book Nook Kit", Url = "https://gofile.io/d/dE6TgA", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP44", Name = "The Sims 4 Poolside Splash Kit", Url = "https://gofile.io/d/pf6hXY", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP45", Name = "The Sims 4 Modern Luxe Kit", Url = "https://gofile.io/d/CXnYZx", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP46", Name = "The Sims 4 Home Chef Hustle Stuff", Url = "https://gofile.io/d/S4VEe5", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP47", Name = "The Sims 4 Castle Estate Kit", Url = "https://gofile.io/d/IF7j1m", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP48", Name = "The Sims 4 Goth Galore Kit", Url = "https://gofile.io/d/Vya1AP", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP49", Name = "The Sims 4 Crystal Creations Stuff", Url = "https://gofile.io/d/qRMf72", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP50", Name = "The Sims 4 Urban Homage Kit", Url = "https://gofile.io/d/4WLJyL", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP51", Name = "The Sims 4 Party Essentials Kit", Url = "https://gofile.io/d/MAYnh0", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP52", Name = "The Sims 4 Riviera Retreat Kit", Url = "https://gofile.io/d/yb4aVz", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP53", Name = "The Sims 4 Cozy Bistro Kit", Url = "https://gofile.io/d/CG0tmO", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP54", Name = "The Sims 4 Artist Studio Kit", Url = "https://gofile.io/d/Uig5Hw", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP55", Name = "The Sims 4 Storybook Nursery Kit", Url = "https://gofile.io/d/IFW7z6", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP56", Name = "The Sims 4 Sweet Slumber Party Kit", Url = "https://gofile.io/d/elOnfx", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP57", Name = "The Sims 4 Cozy Kitsch Kit", Url = "https://gofile.io/d/onQQSP", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP58", Name = "The Sims 4 Comfy Gamer Kit", Url = "https://gofile.io/d/fcJ1Gh", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP59", Name = "The Sims 4 Secret Sanctuary Kit", Url = "https://gofile.io/d/113pyx", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP60", Name = "The Sims 4 Casanova Cave Kit", Url = "https://gofile.io/d/RCzz1t", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP61", Name = "The Sims 4 Refined Living Room Kit", Url = "https://gofile.io/d/TRWX3e", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP62", Name = "The Sims 4 Business Chic Kit", Url = "https://gofile.io/d/z1Jm73", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP63", Name = "The Sims 4 Sleek Bathroom Kit", Url = "https://gofile.io/d/MsQxsP", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP64", Name = "The Sims 4 Sweet Allure Kit", Url = "https://gofile.io/d/CdWkd8", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP65", Name = "The Sims 4 Restoration Workshop Kit", Url = "https://gofile.io/d/cAw8M7", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP66", Name = "The Sims 4 Golden Years Kit", Url = "https://gofile.io/d/H9vnBF", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP67", Name = "The Sims 4 Kitchen Clutter Kit", Url = "https://gofile.io/d/cLDKBY", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP68", Name = "The Sims 4 SpongeBob’s House Kit", Url = "https://gofile.io/d/DlGUn8", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP69", Name = "The Sims 4 Autumn Apparel Kit", Url = "https://gofile.io/d/89av5w", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP70", Name = "The Sims 4 SpongeBob Kid’s Room Kit", Url = "https://gofile.io/d/NETiKy", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP71", Name = "The Sims 4 Grange Mudroom Kit", Url = "https://gofile.io/d/r1qojx", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP72", Name = "The Sims 4 Essential Glam Kit", Url = "https://gofile.io/d/w6UMt5", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP73", Name = "The Sims 4 Modern Retreat Kit", Url = "https://gofile.io/d/ca15lA", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP74", Name = "The Sims 4 Garden to Table Kit", Url = "https://gofile.io/d/NJx0Ci", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP75", Name = "The Sims 4 Wonderland Playroom Kit", Url = "https://gofile.io/d/r30ZDu", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP76", Name = "The Sims 4 Silver Screen Style Kit", Url = "https://gofile.io/d/OyWN68", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP77", Name = "The Sims 4 Tea Time Solarium Kit", Url = "https://gofile.io/d/e9stez", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP78", Name = "The Sims 4 Lady Bridgerton’s Masquerade Ball Fashion Kit", Url = "https://gofile.io/d/8jLQRx", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP79", Name = "The Sims 4 Lady Bridgerton’s Masquerade Ballroom Kit", Url = "https://gofile.io/d/oIHt2t", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP80", Name = "The Sims 4 Music Den Kit", Url = "https://gofile.io/d/S1AQET", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP81", Name = "The Sims 4 Prairie Dreams Set", Url = "https://gofile.io/d/gyYXEQ", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP82", Name = "The Sims 4 Yard Charm (Creator) Kit", Url = "https://gofile.io/d/istzpw", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP83", Name = "The Sims 4 Mean Girls Capsule Kit", Url = "https://gofile.io/d/z2CVXgGG", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP84", Name = "The Sims 4 Clueless Capsule  Kit", Url = "https://gofile.io/d/SM7HSxz4", Installed = false });
        }


        public async Task DownloadAndInstallFromMinioSelectedDlcsAsync(Logger logger, string gamepath)
        {
            using var gofile_downloader = new GoFileDownloader(
                maxRetries: 3, 
                timeout: Timeout.InfiniteTimeSpan
            );

            Task installTask = Task.CompletedTask;

            foreach (var dlc in Sims4DLCs)
            {
                if (dlc.ToInstall && !dlc.Installed)
                {
                    logger.AddLog($"Downloading DLC: {dlc.Code} - {dlc.Name}");


                    // Run the download off the UI thread so its file work doesn't freeze the app
                    bool success = await Task.Run(() => dlc.Download(gofile_downloader, logger));

                    // Small pause between DLCs so GoFile doesn't rate limit us
                    await Task.Delay(TimeSpan.FromSeconds(3));

                    if (!success)
                    {
                        logger.AddLog($"❌ Failed to download DLC: {dlc.Code} - {dlc.Name}");
                        logger.AddLog("Skipping this DLC and continuing with next one...");
                        logger.AddLog("---------------------------------------------");
                        continue;
                    }

                    // Install this DLC in the background after the previous one finishes,
                    // so the next download can start right away
                    Task previousInstall = installTask;
                    installTask = Task.Run(async () =>
                    {
                        await previousInstall;

                        logger.AddLog($"Extracting DLC: {dlc.Code} - {dlc.Name}");
                        dlc.Extract(logger);

                        logger.AddLog($"Installing DLC: {dlc.Code} - {dlc.Name}");
                        dlc.Install(gamepath, logger);

                        logger.AddLog($"✓ Installed DLC: {dlc.Code} - {dlc.Name}");
                        logger.AddLog($"Clearing TEMP files");
                        dlc.Remove(logger);

                        logger.AddLog("---------------------------------------------");
                    });
                }
            }

            await installTask;
            logger.AddLog("All downloads completed.");
        }
      

    }
}
