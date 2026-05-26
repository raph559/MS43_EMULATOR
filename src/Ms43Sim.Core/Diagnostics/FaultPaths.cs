namespace Ms43Sim.Core;

/// <summary>
/// The MS43X001 fault paths (plan 3, Reference facts) [obs]. Each path's name is the XDF's
/// <c>c_dtc_&lt;name&gt;_0</c> constant at its type-0 pointer, without <c>c_dtc_</c> and <c>_0</c>, as in
/// <c>calmaps_ms43x.csv</c>; paths 83, 84 and 157 have none. Each path's DS2 fault code is the
/// first byte of its descriptor; the 162 codes identify the layout.
/// </summary>
public static class FaultPaths
{
    public const int Count = 162;

    static readonly string?[] Names =
    {
        "maf", "crk", "cam", "cam_ex", "vs", "toil", "tco_ex", "tco_stuck", "tco", "tia_plaus",   // 0-9
        "tia", "tco_max", "t_es", "tco_ex_plaus", "vls_up_1", "lam_lim_1", "lam_lim_2", "vls_stk_1", "vls_up_2", "vls_stk_2",   // 10-19
        "lsh_up_1", "lsh_obd_up_1", "lsh_up_2", "lsh_obd_up_2", "lsh_down_1", "lsh_obd_down_1", "lsh_down_2", "lsh_obd_down_2", "cat_diag_1", "vls_down_1",   // 20-29
        "vls_jump_1", "ls_frq_1", "lam_stop_1", "t_lam_act", "lam_dly_up_1", "lam_dly_down_1", "vls_down_act_chk_1", "vls_down_act_chk_2", "cat_diag_2", "vls_down_2",   // 30-39
        "vls_jump_2", "ls_frq_2", "lam_stop_2", "lam_dly_up_2", "lam_dly_down_2", "cps", "iv_0", "iv_1", "iv_2", "iv_3",   // 40-49
        "iv_4", "iv_5", "igcfb_0", "igcfb_1", "igcfb_2", "igcfb_3", "igcfb_4", "igcfb_5", "r_igcfb", "t_igcfb_2",   // 50-59
        "knk_1", "knk_2", "isa_1", "isa_2", "mec_isa", "rly_accout", "rly_efp", "cam_in_ivvt", "ad_mec_ref_ivvt_in", "mec_ivvt_in",   // 60-69
        "cam_ex_ivvt", "ad_mec_ref_ivvt_ex", "mec_ivvt_ex", "ef", "ecu", "can_boff", "tout_etcu_1", "tout_icl_2", "tout_icl_3", "tout_asr_1",   // 70-79
        "tout_asr_3", "tout_amt_1", "tout_pste_1", null, null, "er_ad", "mis_0", "mis_1", "mis_2", "mis_3",   // 80-89
        "mis_4", "mis_5", "sav", "sap", "sa_1", "sa_2", "mec_sav", "rly_main", "pvs_1", "pvs_2",   // 90-99
        "pvs_pvs", "tps_1", "tps_2", "tps_maf_1", "tps_maf_2", "pvs_ratio", "vcc_poti_1", "vcc_poti_2", "mtc_dr", "tps_st_chk_1",   // 100-109
        "tps_st_chk_2", "mtc_ctl_1", "mtc_ctl_2", "mtc_ctl_3", "tps_ad", "tqi_lim", "maf_mafm", "bls_plaus", "pvs_bls_bts_plaus", "ecf",   // 110-119
        "vim", "ect", "msw_2", "msw_3", "msw_tog", "diagcps", "is", "vls_down_t_1", "vls_down_t_2", "vls_down_post_puc_1",   // 120-129
        "vls_down_post_puc_2", "vls_down_afl_1", "vls_down_afl_2", "amp", "var_amp", "teg_up_1", "teg_up_2", "teg_down_2", "ff", "sav_1_safm",   // 130-139
        "sap_safm", "min_saf", "sa_conf", "sav_safm", "cat_eff_1", "cat_eff_2", "cs", "ect_mec", "mon_plaus", "mon_tqi_av",   // 140-149
        "mon_tqi_n_max", "tqi_n_max_nvmy_mon", "dmtl", "vdmtl", "dmtlm", "dmtl_leak", "tam_can", null, "tqi_amt_1", "rly_main_dly",   // 150-159
        "ftl_can", "ftl_obd",   // 160-161
    };

    static readonly byte[] Ds2Codes =
    {
        0x08, 0x53, 0x41, 0x12, 0xD6, 0x7A, 0x0B, 0x11, 0x0A, 0x10,   // 0-9
        0x0E, 0x0C, 0x0F, 0x0D, 0x96, 0xCA, 0xCB, 0xBA, 0x97, 0xBB,   // 10-19
        0x19, 0xBC, 0x37, 0xBD, 0x4F, 0xBE, 0x3D, 0xBF, 0xE9, 0x98,   // 20-29
        0xE7, 0xE5, 0xE3, 0xDE, 0xEB, 0xE1, 0xD7, 0xD8, 0xEA, 0x99,   // 30-39
        0xE8, 0xE6, 0xE4, 0xEC, 0xE2, 0x44, 0x06, 0x21, 0x16, 0x17,   // 40-49
        0x05, 0x18, 0x1D, 0x1F, 0x1E, 0x03, 0x01, 0x02, 0x38, 0xD2,   // 50-59
        0x39, 0x3B, 0x35, 0x1B, 0xD3, 0x4A, 0x45, 0x15, 0x67, 0x69,   // 60-69
        0x13, 0x68, 0x6A, 0x34, 0x64, 0xDB, 0xD9, 0x83, 0x84, 0x82,   // 70-79
        0x85, 0x81, 0x89, 0xD1, 0x80, 0xF4, 0xEE, 0xF2, 0xF0, 0xF3,   // 80-89
        0xEF, 0xF1, 0x3E, 0x23, 0xF5, 0xF6, 0xF7, 0x24, 0x6E, 0x6F,   // 90-99
        0x72, 0x70, 0x71, 0x76, 0x77, 0x75, 0x92, 0x93, 0x6D, 0x87,   // 100-109
        0x88, 0xA0, 0xA1, 0xA2, 0x73, 0x2F, 0x95, 0x27, 0x78, 0x7D,   // 110-119
        0x7C, 0x7B, 0x2A, 0x2B, 0x2D, 0xFA, 0xCC, 0xDC, 0xDD, 0xF8,   // 120-129
        0xF9, 0xDF, 0xE0, 0xC5, 0xC4, 0x5A, 0x5B, 0x5D, 0x5E, 0x5F,   // 130-139
        0x60, 0x61, 0x62, 0x63, 0xC6, 0xC7, 0x26, 0xA8, 0x30, 0x31,   // 140-149
        0x32, 0x33, 0x8C, 0x7E, 0x8E, 0x8F, 0x3F, 0x40, 0x86, 0x25,   // 150-159
        0x8B, 0x8D,   // 160-161
    };

    /// <summary>The path's XDF name; null for 83, 84 and 157.</summary>
    public static string? Name(int path) => Names[path];

    public static byte Ds2Code(int path) => Ds2Codes[path];
}
