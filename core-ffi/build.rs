//! Regenerates include/wormhole.h from src/lib.rs with cbindgen. The header
//! is committed, so the apps build without running this.

fn main() {
    println!("cargo:rerun-if-changed=src/lib.rs");
    println!("cargo:rerun-if-changed=cbindgen.toml");
    let dir = std::env::var("CARGO_MANIFEST_DIR").expect("set by cargo");
    match cbindgen::generate(&dir) {
        Ok(bindings) => {
            bindings.write_to_file(format!("{dir}/include/wormhole.h"));
        }
        Err(err) => println!("cargo:warning=cbindgen could not write the header: {err}"),
    }
}
