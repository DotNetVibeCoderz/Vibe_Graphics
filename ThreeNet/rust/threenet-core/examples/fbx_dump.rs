//! Prints the models of an FBX file: `cargo run --example fbx_dump -- file.fbx`
fn main() {
    let path = std::env::args().nth(1).expect("path");
    let data = std::fs::read(&path).unwrap();
    let document = threenet_core::fbx::parse(&data).unwrap();
    for node in &document.child("Objects").unwrap().children {
        if node.name == "Model" || node.name == "Geometry" {
            println!("{} {:?}", node.name, node.properties.iter().take(3).collect::<Vec<_>>());
            if let Some(p) = node.child("Properties70") {
                for prop in &p.children {
                    println!("    {:?}", prop.properties);
                }
            }
        }
    }
    if let Some(g) = document.child("GlobalSettings").and_then(|g| g.child("Properties70")) {
        for prop in g.children.iter().take(10) {
            println!("G {:?}", prop.properties);
        }
    }
}
