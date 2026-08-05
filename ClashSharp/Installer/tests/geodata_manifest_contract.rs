use std::collections::BTreeMap;

#[allow(dead_code, unused_imports)]
#[path = "../build.rs"]
mod build_script;

const HASH_A: &str = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
const HASH_B: &str = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
const HASH_C: &str = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
const HASH_D: &str = "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";

fn trusted_geodata() -> BTreeMap<String, (u64, String)> {
    BTreeMap::from([
        (
            String::from("binaries/geodata/country.mmdb"),
            (11, String::from(HASH_A)),
        ),
        (
            String::from("binaries/geodata/geoip.dat"),
            (22, String::from(HASH_B)),
        ),
        (
            String::from("binaries/geodata/geosite.dat"),
            (33, String::from(HASH_C)),
        ),
        (
            String::from("binaries/geodata/asn.mmdb"),
            (44, String::from(HASH_D)),
        ),
    ])
}

fn exact_manifest() -> Vec<u8> {
    serde_json::to_vec(&serde_json::json!({
        "schemaVersion": 1,
        "files": [
            { "name": "Country.mmdb", "length": 11, "sha256": HASH_A },
            { "name": "GeoIP.dat", "length": 22, "sha256": HASH_B },
            { "name": "GeoSite.dat", "length": 33, "sha256": HASH_C },
            { "name": "ASN.mmdb", "length": 44, "sha256": HASH_D }
        ]
    }))
    .unwrap()
}

#[test]
fn exact_manifest_matches_final_msix_assets() {
    assert!(build_script::validate_geodata_manifest(&exact_manifest(), &trusted_geodata()).is_ok());
}

#[test]
fn manifest_rejects_content_hash_or_shape_drift() {
    let mut wrong_hash = trusted_geodata();
    wrong_hash.get_mut("binaries/geodata/geoip.dat").unwrap().1 = String::from(HASH_A);
    let error =
        build_script::validate_geodata_manifest(&exact_manifest(), &wrong_hash).unwrap_err();
    assert!(error.contains("does not match MSIX content"));

    let unknown_field = serde_json::to_vec(&serde_json::json!({
        "schemaVersion": 1,
        "unexpected": true,
        "files": [
            { "name": "Country.mmdb", "length": 11, "sha256": HASH_A },
            { "name": "GeoIP.dat", "length": 22, "sha256": HASH_B },
            { "name": "GeoSite.dat", "length": 33, "sha256": HASH_C },
            { "name": "ASN.mmdb", "length": 44, "sha256": HASH_D }
        ]
    }))
    .unwrap();
    let error =
        build_script::validate_geodata_manifest(&unknown_field, &trusted_geodata()).unwrap_err();
    assert!(error.contains("parse GeoData manifest failed"));
}
