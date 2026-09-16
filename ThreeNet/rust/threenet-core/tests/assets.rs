//! Texture streaming and the asset cache.

use std::path::PathBuf;
use std::time::Duration;

use threenet_core::assets::StreamState;
use threenet_core::scene::Scene;
use threenet_core::texture::TextureFormat;

fn asset(name: &str) -> String {
    format!("{}/tests/assets/{name}", env!("CARGO_MANIFEST_DIR"))
}

fn write_png(name: &str, size: u32, color: [u8; 4]) -> PathBuf {
    let dir = std::env::temp_dir().join(format!("threenet-assets-{}", std::process::id()));
    std::fs::create_dir_all(&dir).unwrap();
    let path = dir.join(name);
    image::RgbaImage::from_pixel(size, size, image::Rgba(color)).save(&path).unwrap();
    path
}

#[test]
fn streamed_textures_swap_in_after_loading() {
    let red = write_png("red.png", 256, [255, 0, 0, 255]);
    let blue = write_png("blue.png", 64, [0, 0, 255, 255]);
    let mut scene = Scene::new();
    scene.set_streaming_uploads_per_frame(1);

    let a = scene.load_texture_async(red.to_str().unwrap(), true).unwrap();
    let b = scene.load_texture_async(blue.to_str().unwrap(), false).unwrap();
    assert_eq!(scene.load_texture_async(red.to_str().unwrap(), true).unwrap(), a, "same path is cached");
    assert_ne!(scene.load_texture_async(red.to_str().unwrap(), false).unwrap(), a, "colour space is part of the key");

    // Placeholder right away.
    let placeholder = scene.texture(a).unwrap();
    assert_eq!((placeholder.width, placeholder.height), (1, 1));
    assert_eq!(scene.texture_state(a), StreamState::Loading);

    assert!(scene.finish_streaming(Duration::from_secs(20)));
    assert_eq!(scene.texture_state(a), StreamState::Ready);
    assert_eq!(scene.texture_state(b), StreamState::Ready);
    let loaded = scene.texture(a).unwrap();
    assert_eq!((loaded.width, loaded.height, loaded.format), (256, 256, TextureFormat::Rgba8UnormSrgb));
    assert_eq!(&loaded.pixels[..4], &[255, 0, 0, 255]);
    assert_eq!(scene.texture(b).unwrap().format, TextureFormat::Rgba8Unorm);

    let stats = scene.asset_stats();
    assert_eq!(stats.pending_textures, 0);
    assert_eq!(stats.streamed_textures, 3);
    assert_eq!(stats.cache_hits, 1);

    assert!(scene.load_texture_async("missing.png", true).is_err());
    assert_eq!(scene.texture_state(9999), StreamState::Missing);
}

#[test]
fn broken_files_report_failure_and_removed_textures_are_ignored() {
    let dir = std::env::temp_dir().join(format!("threenet-assets-bad-{}", std::process::id()));
    std::fs::create_dir_all(&dir).unwrap();
    let broken = dir.join("broken.png");
    std::fs::write(&broken, b"definitely not a png").unwrap();
    let good = write_png("green.png", 32, [0, 255, 0, 255]);

    let mut scene = Scene::new();
    let bad = scene.load_texture_async(broken.to_str().unwrap(), true).unwrap();
    let removed = scene.load_texture_async(good.to_str().unwrap(), true).unwrap();
    assert!(scene.remove_texture(removed));
    assert!(scene.finish_streaming(Duration::from_secs(20)));

    assert_eq!(scene.texture_state(bad), StreamState::Failed);
    assert!(scene.texture_error(bad).is_some());
    assert_eq!(scene.texture_state(removed), StreamState::Missing);
}

#[test]
fn cached_textures_and_models_are_shared() {
    let png = write_png("cached.png", 16, [10, 20, 30, 255]);
    let mut scene = Scene::new();
    let first = scene.load_texture_cached(png.to_str().unwrap(), true).unwrap();
    assert_eq!(scene.load_texture_cached(png.to_str().unwrap(), true).unwrap(), first);

    let one = scene.load_model_cached(&asset("phong_cube.fbx"), None).unwrap();
    let textures_after_first = scene.stats().textures;
    let geometries_after_first = scene.stats().geometries;
    let two = scene.load_model_cached(&asset("phong_cube.fbx"), None).unwrap();
    assert_ne!(one.root, two.root);
    assert_eq!(scene.stats().geometries, geometries_after_first, "clones share geometry");
    assert_eq!(scene.stats().textures, textures_after_first);
    assert_eq!(scene.node(two.root).unwrap().parent(), Some(scene.root()));
    assert!(scene.is_visible_in_hierarchy(two.root));

    // Removing a placed instance keeps the prototype.
    scene.remove_node(one.root).unwrap();
    let three = scene.load_model_cached(&asset("phong_cube.fbx"), None).unwrap();
    assert_eq!(scene.stats().geometries, geometries_after_first);
    assert_eq!(three.nodes.len(), two.nodes.len());

    // Animated models are imported each time, never hidden.
    let rig = scene.load_model_cached(&asset("BoxAnimated.glb"), None).unwrap();
    assert!(!rig.animations.is_empty());
    assert!(scene.is_visible_in_hierarchy(rig.root));

    let stats = scene.asset_stats();
    assert_eq!(stats.cached_textures, 1);
    assert_eq!(stats.cached_models, 1);
    assert_eq!(stats.cache_hits, 3);

    scene.clear_asset_cache();
    assert_eq!(scene.asset_stats().cached_models, 0);
    assert!(scene.node(two.root).is_some(), "placed instances survive clearing the cache");
}
