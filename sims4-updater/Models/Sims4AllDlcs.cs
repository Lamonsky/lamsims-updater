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
            Sims4DLCs.Add(new Sims4DLC { Code = "EP01", Name = "The Sims 4 Get to Work", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_EP01_Get_to_Work.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "EP02", Name = "The Sims 4 Get Together", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_EP02_Get_Together.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "EP03", Name = "The Sims 4 City Living", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_EP03_City_Living.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "EP04", Name = "The Sims 4 Cats & Dogs", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_EP04_Cats_and_Dogs.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "EP05", Name = "The Sims 4 Seasons", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_EP05_Seasons.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "EP06", Name = "The Sims 4 Get Famous", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_EP06_Get_Famous.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "EP07", Name = "The Sims 4 Island Living", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_EP07_Island_Living.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "EP08", Name = "The Sims 4 Discover University", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_EP08_Discover_University.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "EP09", Name = "The Sims 4 Eco Lifestyle", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_EP09_Eco_Lifestyle.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "EP10", Name = "The Sims 4 Snowy Escape", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_EP10_Snowy_Escape.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "EP11", Name = "The Sims 4 Cottage Living", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_EP11_Cottage_Living.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "EP12", Name = "The Sims 4 High School Years", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_EP12_High_School_Years.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "EP13", Name = "The Sims 4 Growing Together", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_EP13_Growing_Together.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "EP14", Name = "The Sims 4 Horse Ranch", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_EP14_Horse_Ranch.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "EP15", Name = "The Sims 4 For Rent", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_EP15_For_Rent.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "EP16", Name = "The Sims 4 Lovestruck", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_EP16_Lovestruck.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "EP17", Name = "The Sims 4 Life & Death", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_EP17_Life_and_Death.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "EP18", Name = "The Sims 4 Businesses & Hobbies", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_EP18_Businesses_and_Hobbies.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "EP19", Name = "The Sims 4 Enchanted by Natures", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_EP19_Enchanted_by_Nature_Expansion_Pack.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "EP20", Name = "The Sims 4 Adventure Awaits", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_EP20_Adventure_Awaits_Expansion_Pack.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "EP21", Name = "The Sims 4 Royalty & Legacy", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_EP21_Royalty & Legacy.zip", Installed = false });
            //Pakiety (GP)
            Sims4DLCs.Add(new Sims4DLC { Code = "GP01", Name = "The Sims 4 Outdoor Retreat", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_GP01_Outdoor_Retreat.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "GP02", Name = "The Sims 4 Spa Day", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_GP02_Spa_Day.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "GP03", Name = "The Sims 4 Dine Out", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_GP03_Dine_Out.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "GP04", Name = "The Sims 4 Vampires", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_GP04_Vampires.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "GP05", Name = "The Sims 4 Parenthood", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_GP05_Parenthood.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "GP06", Name = "The Sims 4 Jungle Adventure", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_GP06_Jungle_Adventure.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "GP07", Name = "The Sims 4 StrangerVille", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_GP07_StrangerVille.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "GP08", Name = "The Sims 4 Realm of Magic", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_GP08_Realm_of_Magic.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "GP09", Name = "The Sims 4 Star Wars: Journey to Batuu", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_GP09_Star_Wars_Journey_to_Batuu.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "GP10", Name = "The Sims 4 Dream Home Decorator", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_GP10_Dream_Home_Decorator.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "GP11", Name = "The Sims 4 My Wedding Stories", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_GP11_My_Wedding_Stories.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "GP12", Name = "The Sims 4 Werewolves", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_GP12_Werewolves.zip", Installed = false });
            //Akcesoria (SP)
            Sims4DLCs.Add(new Sims4DLC { Code = "SP01", Name = "The Sims 4 Luxury Party Stuff", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_SP01_Luxury_Party_Stuff.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP02", Name = "The Sims 4 Perfect Patio Stuff", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_SP02_Perfect_Patio_Stuff.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP03", Name = "The Sims 4 Cool Kitchen Stuff", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_SP03_Cool_Kitchen_Stuff.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP04", Name = "The Sims 4 Spooky Stuff", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_SP04_Spooky_Stuff.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP05", Name = "The Sims 4 Movie Hangout Stuff", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_SP05_Movie_Hangout_Stuff.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP06", Name = "The Sims 4 Romantic Garden Stuff", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_SP06_Romantic_Garden_Stuff.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP07", Name = "The Sims 4 Kids Room Stuff", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_SP07_Kids_Room_Stuff.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP08", Name = "The Sims 4 Backyard Stuff", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_SP08_Backyard_Stuff.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP09", Name = "The Sims 4 Vintage Glamour Stuff", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_SP09_Vintage_Glamour_Stuff.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP10", Name = "The Sims 4 Bowling Night Stuff", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_SP10_Bowling_Night_Stuff.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP11", Name = "The Sims 4 Fitness Stuff", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_SP11_Fitness_Stuff.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP12", Name = "The Sims 4 Toddler Stuff", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_SP12_Toddler_Stuff.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP13", Name = "The Sims 4 Laundry Day Stuff", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_SP13_Laundry_Day_Stuff.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP14", Name = "The Sims 4 My First Pet Stuff", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_SP14_My_First_Pet_Stuff.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP15", Name = "The Sims 4 Moschino Stuff", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_SP15_Moschino_Stuff.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP16", Name = "The Sims 4 Tiny Living Stuff", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_SP16_Tiny_Living_Stuff_Pack.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP17", Name = "The Sims 4 Nifty Knitting Stuff", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_SP17_Nifty_Knitting.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP18", Name = "The Sims 4 Paranormal Stuff", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_SP18_Paranormal_Stuff_Pack.zip", Installed = false });
            // Kolekcje (Kits)
            Sims4DLCs.Add(new Sims4DLC { Code = "SP20", Name = "The Sims 4 Throwback Fit Kit", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_SP20_Throwback_Fit_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP21", Name = "The Sims 4 Country Kitchen Kit", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_SP21_Country_Kitchen_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP22", Name = "The Sims 4 Bust the Dust Kit", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_SP22_Bust_the_Dust_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP23", Name = "The Sims 4 Courtyard Oasis Kit", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_SP23_Courtyard_Oasis_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP24", Name = "The Sims 4 Fashion Street Kit", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_SP24_Fashion_Street_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP25", Name = "The Sims 4 Industrial Loft Kit", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_SP25_Industrial_Loft_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP26", Name = "The Sims 4 Incheon Arrivals Kit", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_SP26_Incheon_Arrivals_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP28", Name = "The Sims 4 Modern Menswear Kit", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_SP28_Modern_Menswear_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP29", Name = "The Sims 4 Blooming Rooms Kit", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_SP29_Blooming_Rooms_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP30", Name = "The Sims 4 Carnaval Streetwear Kit", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_SP30_Carnaval_Streetwear_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP31", Name = "The Sims 4 Décor to the Max Kit", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_SP31_Decor_to_the_Max_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP32", Name = "The Sims 4 Moonlight Chic Kit", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_SP32_Moonlight_Chic_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP33", Name = "The Sims 4 Little Campers Kit", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_SP33_Little_Campers_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP34", Name = "The Sims 4 First Fits Kit", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_SP34_First_Fits_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP35", Name = "The Sims 4 Desert Luxe Kit", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_SP35_Desert_Luxe_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP36", Name = "The Sims 4 Pastel Pop Kit", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_SP36_Pastel_Pop_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP37", Name = "The Sims 4 Everyday Clutter Kit", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_SP37_Everyday_Clutter_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP38", Name = "The Sims 4 Simtimates Collection Kit", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_SP38_Simtimates_Collection_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP39", Name = "The Sims 4 Bathroom Clutter Kit", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_SP39_Bathroom_Clutter_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP40", Name = "The Sims 4 Greenhouse Haven Kit", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_SP40_Greenhouse_Haven_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP41", Name = "The Sims 4 Basement Treasures Kit", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_SP41_Basement_Treasures_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP42", Name = "The Sims 4 Grunge Revival Kit", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_SP42_Grunge_Revival_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP43", Name = "The Sims 4 Book Nook Kit", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_SP43_Book_Nook_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP44", Name = "The Sims 4 Poolside Splash Kit", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_SP44_Poolside_Splash_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP45", Name = "The Sims 4 Modern Luxe Kit", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_SP45_Modern_Luxe_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP46", Name = "The Sims 4 Home Chef Hustle Stuff", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_SP46_Home_Chef_Hustle_Stuff_Pack.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP47", Name = "The Sims 4 Castle Estate Kit", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_SP47_Castle_Estate_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP48", Name = "The Sims 4 Goth Galore Kit", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_SP48_Goth_Galore_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP49", Name = "The Sims 4 Crystal Creations Stuff", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_SP49_Crystal_Creations_Stuff_Pack.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP50", Name = "The Sims 4 Urban Homage Kit", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_SP50_Urban_Homage_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP51", Name = "The Sims 4 Party Essentials Kit", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_SP51_Party_Essentials_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP52", Name = "The Sims 4 Riviera Retreat Kit", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_SP52_Riviera_Retreat_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP53", Name = "The Sims 4 Cozy Bistro Kit", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_SP53_Cozy_Bistro_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP54", Name = "The Sims 4 Artist Studio Kit", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_SP54_Artist_Studio_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP55", Name = "The Sims 4 Storybook Nursery Kit", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_SP55_Storybook_Nursery_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP56", Name = "The Sims 4 Sweet Slumber Party Kit", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_SP56_Sweet_Slumber_Party_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP57", Name = "The Sims 4 Cozy Kitsch Kit", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_SP57_Cozy_Kitsch_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP58", Name = "The Sims 4 Comfy Gamer Kit", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_SP58_Comfy_Gamer_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP59", Name = "The Sims 4 Secret Sanctuary Kit", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_SP59_Secret_Sanctuary_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP60", Name = "The Sims 4 Casanova Cave Kit", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_SP60_Casanova_Cave_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP61", Name = "The Sims 4 Refined Living Room Kit", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_SP61_Refined_Living_Room_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP62", Name = "The Sims 4 Business Chic Kit", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_SP62_Business_Chic_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP63", Name = "The Sims 4 Sleek Bathroom Kit", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_SP63_Sleek_Bathroom_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP64", Name = "The Sims 4 Sweet Allure Kit", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_SP64_Sweet_Allure_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP65", Name = "The Sims 4 Restoration Workshop Kit", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_SP65_Restoration_Workshop_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP66", Name = "The Sims 4 Golden Years Kit", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_SP66_Golden_Years_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP67", Name = "The Sims 4 Kitchen Clutter Kit", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_SP67_Kitchen_Clutter_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP68", Name = "The Sims 4 SpongeBob’s House Kit", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_SP68_SpongeBob_House_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP69", Name = "The Sims 4 Autumn Apparel Kit", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_SP69_Autumn_Apparel_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP70", Name = "The Sims 4 SpongeBob Kid’s Room Kit", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_SP70_SpongeBob_Kid_Room_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP71", Name = "The Sims 4 Grange Mudroom Kit", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_SP71_Grange_Mudroom_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP72", Name = "The Sims 4 Essential Glam Kit", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_SP72_Essential_Glam_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP73", Name = "The Sims 4 Modern Retreat Kit", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_SP73_Modern_Retreat_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP74", Name = "The Sims 4 Garden to Table Kit", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_SP74_Garden_to_Table_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP76", Name = "The Sims 4 Silver Screen Style Kit", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_SP76_Silver Screen Style Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP77", Name = "The Sims 4 Tea Time Solarium Kit", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_SP77_Tea Time Solarium Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP81", Name = "The Sims 4 Prairie Dreams Set", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_SP81_Prairie_Dreams_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP75", Name = "The Sims 4 Wonderland Playroom Kit", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_SP75_Wonderland_Playroom_Kit.zip", Installed = false });
            Sims4DLCs.Add(new Sims4DLC { Code = "SP82", Name = "The Sims 4 Yard Charm (Creator) Kit", Url = "https://minio.lamonski.pl/sims4dlcs/My_Private_Stuff_SP82_Yard_Charm_Kit.zip", Installed = false });
        }


        public async Task DownloadAndInstallFromMinioSelectedDlcsAsync(Logger logger, string gamepath)
        {

            foreach (var dlc in Sims4DLCs)
            {
                if (dlc.ToInstall && !dlc.Installed)
                {
                    logger.AddLog($"Downloading DLC: {dlc.Code} - {dlc.Name}");

                    await dlc.Download(logger);

                    logger.AddLog($"Extracting DLC: {dlc.Code} - {dlc.Name}");
                    await Task.Run(() => dlc.Extract(logger));

                    logger.AddLog($"Installing DLC: {dlc.Code} - {dlc.Name}");
                    await Task.Run(() => dlc.Install(gamepath, logger));

                    logger.AddLog($"Installed DLC: {dlc.Code} - {dlc.Name}");
                    logger.AddLog($"Clearing TEMP files");
                    await Task.Run(() => dlc.Remove(logger));

                    logger.AddLog("---------------------------------------------");
                }
            }

            logger.AddLog("All downloads completed. Logged out from Mega.");
        }
      

    }
}
