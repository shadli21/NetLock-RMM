namespace NetLock_RMM_Web_Console
{
    public class Application_Settings
    {
        public static string version = "2.6.1.2"; // Used to check if the web console is up to date through the Members Portal API
        public static string db_version = "2.6.1.2"; // Used to trigger the changelog dialog
        public static string web_console_version = "Penguin Rising 2.6.1.2"; // Displayed on the top nav bar 
        public static string versionUrl = "https://netlockrmm.com/blog"; // The link used in the top navbar if the user clicks on the version number
        public static string Local_Encryption_Key = "01234567890123456789012345678901";

        //OSSCH_START 123c8d2a-f552-40b0-9944-13e8dd41924d //OSSCH_END

        public static string onlyPro = "This feature is exclusive to Pro & Cloud users. Please ensure you have an active paid membership, or your changes will not take effect.";
    }
}