using System.Text.RegularExpressions;

namespace TerrainTool.Core.Campaign;

/// <summary>Blocco di produzione di un mezzo o di un'arma (la vecchia divisione occidentale / sovietica delle campagne Falcon).</summary>
public enum Bloc { Unknown, West, East, China }

/// <summary>
/// Produttore dei mezzi e delle armi. Nel database (CT/VCD/WCD) il produttore non c'e': lo si ricava dal nome, con le
/// sigle tipiche (SA-, ZSU, T-72, BMP, MiG, Su-, R-, Kh- = sovietico/russo; J-, ZTZ, PL-, HQ-, YJ- = cinese;
/// M1A1, MIM, AIM, AGM, Leopard, Roland, Mirage... = occidentale). Le squadre hanno invece il codice Equipment nel .tea
/// (come Korea: 1 cinese, 2 nordcoreano, 3 sudcoreano, 4 sovietico/russo, 5 USA/occidentale).
/// </summary>
public static class Producer
{
    public static readonly string[] EquipmentNames = { "None", "Chinese", "North Korean", "South Korean", "Soviet / Russian", "US / Western" };

    public static Bloc OfEquipment(int eq) => eq switch { 1 => Bloc.China, 2 => Bloc.East, 3 => Bloc.West, 4 => Bloc.East, 5 => Bloc.West, _ => Bloc.Unknown };

    public static string Short(Bloc b) => b switch { Bloc.West => "West", Bloc.East => "Soviet", Bloc.China => "Chinese", _ => "?" };

    // l'ordine conta: prima le regole piu' specifiche
    private static readonly (Regex Re, Bloc B)[] Rules =
    {
        // cinese (prima di J-/Type che altrimenti sarebbero ambigui)
        (R(@"^(J|JH|JF|JL|FC|Q|H|Y|Z|KJ|WZ|WJ|GJ)-?\d"), Bloc.China),
        (R(@"\b(ZTZ|ZBD|ZSL|ZLC|PLZ|PHL|PGZ|HQ|PL|YJ|C-?80\d|C-?70\d|LS|LT|FT|KD|CJ|DF|LD|TL|QW|SD)-?\d|\bKS-1[A-Z]?\b|\bHN-?5|\bJ-?\d+[A-Z]*\b"), Bloc.China),
        (R(@"\bTYPE\s?0\d\d|\bTYPE\s?(05|09)\d|\bLUYANG|\bJIANGKAI|\bJIANGHU|\bLUDA|\bSONG\b|\bYUAN\b|\bMING\b|\bHAN\b|\bSHANG\b|\bTYPE 001|\bLIAONING|\bSHANDONG|\bPRC\b|\bPLA\b"), Bloc.China),
        (R(@"\bTYPE\s?(59|62|63|69|79|85|88|96|99|6\d\d)\b"), Bloc.China),
        // nordcoreano -> blocco orientale
        (R(@"SAETBYOL|SINPO|KOKSAN|CHONMA|PONGAE|HWASONG|NODONG|M-?19(78|85|91|92)\b|\bVTT-?323|\bDPRK\b|KPA"), Bloc.East),
        // sovietico / russo
        (R(@"\bSA-?N?-?\d{1,2}\b|\bS-?(75|125|200|300|400)\b|\b[29]K\d|\b2S\d|\b2A\d|\bZSU|\bZU-?\d|\bZPU|\bS-?60\b|\bKS-?\d|\bBM-?\d|\bBMP|\bBMD|\bBTR|\bBRDM|\bMT-?LB|\bPT-?76|\bT-?(10|34|54|55|62|64|72|80|90|14)\b"), Bloc.East),
        (R(@"\bMIG|\bSU-?\d|\bTU-?\d|\bIL-?\d|\bAN-?\d|\bMI-?\d|\bKA-?\d{2}|\bYAK-?\d|\bBE-?\d|\bA-50\b"), Bloc.East),
        (R(@"\bR-?\d|\bKH-?\d|\bKAB|\bFAB|\bOFAB|\bRBK|\bBETAB|\bODAB|\bZAB|\bS-?(5|8|13|24|25)\b|\bB-?8M|\bUB-?\d|\bGSH|\bGSH|\bUPK|\bAT-?\d|\b9M\d|\b48N|\bP-?\d{2,3}\b|\b3M\d|\bSS-N|\bSS-?\d|\bSCUD|\bFROG|\bTOCHKA|\bISKANDER"), Bloc.East),
        (R(@"\bAK-?\d|\bRPK|\bPKM|\bDSHK|\bSVD|\bRPG|\bKRAZ|\bZIL-?\d|\bGAZ-?\d|\bURAL-?\d|\bUAZ-?\d|\bMAZ-?\d|\bKAMAZ|\bKILO\b|\bROMEO\b|\bAKULA|\bGOLF\b|\bALFA\b|\bALPHA\b|\bOSA\b|\bKUZNETSOV|\bSLAVA|\bKIROV|\bSOVREMENN|\bUDALOY|\bKRIVAK|\bNANUCHKA|\bTARANTUL|\bGRISHA|\bKOMAR|\bD-?30\b|\bM-?46\b|\bD-?20\b|\bCIS\b|\bSOVIET|\bRUSSIA"), Bloc.East),
        (R(@"\b\d\d-K\b|\(M-?19\d\d\)|\bM-?19[3-9]\d\b|SPOON REST|FLAT FACE|FIRE CAN|LOW BLOW|BIG BIRD|TOMBSTONE|DOG EAR|SNOW DRIFT|FLAP LID|BAR LOCK|SQUARE PAIR|ODD GROUP|LONG TRACK|PAT HAND|STRAIGHT FLUSH|CLAM ?SHELL|\b(54K6|83M6|76N6|76N5|40V6|64N6|96L6|30N6|5N6|1L1)|\b2T7|\bMDK-|\bMT-55|\bATZ-|\bCCCP|\bPTB-|\bAPU-|\bAPK-|\bBD\d-|\bMBD\d|\bSORBTSIYA|\bSAPSAN|\bKMGU|\bU-?O-?25|\b16S\d|\bL-?08\d|\bNR-?\d|\bKPV\b|\bPKT\b|\bDEGTYAREV|\bL-?(29|39|59)\b|\bAS-\d|\bAS-\d+ \("), Bloc.East),
        // occidentale
        (R(@"\bSA-?3\d\d|GAZELLE|\bPUMA\b|FRELON|ALOUETTE|\bLYNX\b|SEA ?KING|WESTLAND|\bBO-?105|\bHOT\b|\bMILAN\b|\bKA-6|\bAML\b|\bFV-?\d|\bG-?[56]\b|\bEE-?\d|\bERC\b|ASTROS|HUNTER|BUCCANEER|ETANDARD|ETENDARD|\bSAMP\b|\bCPU-|\bNORD\b|\bVAB\b|PANHARD|ENGESA|CASCAVEL|URUTU|ALPHA ?JET"), Bloc.West),
        (R(@"\b(F|A|B|C|E|KC|EA|EF|RC|MC|HC|AC|AH|UH|CH|MH|OH|SH|HH|AV|MV|CV|OV|RF|TA|AT|T|U|SR|S|P)-?\d|\bF/A|\bAV-?8|\bCF-?\d|\bKF-?\d|\bF-CK"), Bloc.West),
        (R(@"\bMIM|\bFIM|\bAIM|\bAGM|\bGBU|\bCBU|\bBLU|\bMK-?\d|\bLAU|\bSUU|\bALQ|\bAAQ|\bASQ|\bHARM|\bJDAM|\bJSOW|\bJASSM|\bSLAM|\bTLAM|\bTOMAHAWK|\bHARPOON|\bMAVERICK|\bSIDEWINDER|\bSPARROW|\bAMRAAM|\bPHOENIX|\bHELLFIRE|\bTOW\b|\bSTINGER|\bMISTRAL|\bEXOCET|\bSTORM|\bSCALP|\bMATRA|\bMAGIC|\bMICA|\bMETEOR|\bIRIS|\bASRAAM|\bPYTHON|\bDERBY|\bPOPEYE|\bSPICE|\bHVAR|\bCRV7|\bHYDRA|\bZUNI|\bRIM-?\d|\bRGM|\bUGM|\bSM-?\d|\bESSM|\bSEA ?SPARROW|\bPATRIOT|\bHAWK\b|\bNIKE|\bROLAND|\bRAPIER|\bCROTALE|\bSKYGUARD|\bAVENGER|\bCHAPARRAL|\bVULCAN|\bOERLIKON|\bBOFORS|\bAPACHE|\bDURANDAL|\bBELOUGA|\bBL755|\bALARM\b|\bSEA ?SKUA|\bSEA ?EAGLE|\bPENGUIN|\bKORMORAN|\bRBS|\bKEPD|\bTAURUS"), Bloc.West),
        (R(@"\bM-?\d|\bLAV|\bHMMWV|\bHUMMER|\bLEOPARD|\bGEPARD|\bMARDER|\bCHALLENGER|\bCHIEFTAIN|\bWARRIOR|\bSCIMITAR|\bMERKAVA|\bAMX|\bVAB\b|\bAAV|\bBRADLEY|\bABRAMS|\bSTRYKER|\bK\d|\bK-?\d|\bKSAM|\bCHUN-?MA|\bCHEONGOON|\bBIHO|\bTYPE\s?(10|74|89|90|87)\b|\bMIRAGE|\bRAFALE|\bTORNADO|\bJAGUAR|\bHARRIER|\bTYPHOON\s?FGR|\bEUROFIGHTER|\bGRIPEN|\bVIGGEN|\bDRAKEN|\bHAWK\s?T|\bALPHA\s?JET|\bMB-??339|\bETENDARD|\bSUPER\s?ETENDARD|\bTRANSALL|\bNIMROD|\bSENTRY|\bVC-?10|\bTRISTAR|\bAJ-?37|\bJA-?37|\bJ-?35\b|\bEF-?18|\bTAIFUN|\bAN/|\bFGM|\bGDF|\bMD-?500|\bMQ-?\d|\bRQ-?\d|\bKDC|\bNAMER|\bCENTURION|\bPZH|\bTHMV|\bAAM-\d|\bASM-\d|\bBOZ|\bSKY ?SHADOW|\bBL-?75|\bJP-?233|\bBGL|\bBLG|\bBAP-|\bBAT-|\bAS\.\d|\bASMP|\bSUPER 530|\bR\.?530|\bSKYFLASH|\bBRIMSTONE|\bDAMOCLES|\bPDLCT|\bATLIS|\bDELILAH|\bSBU|\bBDU|\bGPU|\bSPS-|\bRPL-|\bBLACK SHAHEEN|\bAL TARIQ|\bASROC|\bAEROS|\bCERBERUS|\bOTO|\bDEFA|\bBK ?27|\bGAU|\bPHALANX|\bGOALKEEPER|\bSEA ?RAM|\bLITENING|\bSNIPER|\bLANTIRN|\bPAVEWAY|\bTARS|\bTIALD"), Bloc.West),
        (R(@"\bBURKE|\bTICON|\bPERRY|\bNIMITZ|\bCVN|\bIOWA|\bWASP|\bNEWPORT|\bTARAWA|\bSPRUANCE|\bKIDD|\bBELKNAP|\bCALIFORNIA|\bVIRGINIA|\bVIRGINA|\bLOS\s?ANGELES|\bSEAWOLF|\bOHIO|\bOSUMI|\bOYASHIO|\bKONGO|\bULSAN|\bCHAMSURI|\bPOHANG|\bSEJONG|\bDOKDO|\bCLEMENCEAU|\bDE\s?GAULLE|\bINVINCIBLE|\bLEANDER|\bTYPE\s?(22|23|42|45|16)\b|\bLEAHY|\bFARRAGUT|\bKNOX|\bSHIRANE|\bHARUNA|\bGWANGGAETO|\bCHUNGMUGONG|\bKIDD|\bATAGO|\bHYUGA|\bU\.?S\.?|\bUSN|\bUSAF|\bUSMC|\bROK|\bJASDF|\bJMSDF|\bNATO|\bIAF\b|\bRSAF|\bRJAF|\bEAF\b"), Bloc.West),
    };

    private static Regex R(string p) => new(p, RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Blocco dal nome del mezzo o dell'arma; Unknown se il nome non dice niente (camion generici, edifici, "Infantry Squad"...).</summary>
    public static Bloc OfName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return Bloc.Unknown;
        name = name.Trim();
        if (name.StartsWith("R ")) { var rack = OfName(name[2..]); if (rack != Bloc.Unknown) return rack; }   // "R xxx" = rastrelliera per xxx
        if (name.Equals("Typhoon", StringComparison.OrdinalIgnoreCase)) return Bloc.East;               // il sottomarino (il caccia e' "Typhoon FGR.4")
        foreach (var (re, b) in Rules) if (re.IsMatch(name)) return b;
        return Bloc.Unknown;
    }

    /// <summary>Punteggio per i suggerimenti: stesso blocco +25, cinese vs sovietico +10 (materiale affine), blocchi opposti 0.</summary>
    public static int Affinity(Bloc a, Bloc b)
    {
        if (a == Bloc.Unknown || b == Bloc.Unknown) return 8;
        if (a == b) return 25;
        if ((a == Bloc.East && b == Bloc.China) || (a == Bloc.China && b == Bloc.East)) return 10;
        return 0;
    }
}
