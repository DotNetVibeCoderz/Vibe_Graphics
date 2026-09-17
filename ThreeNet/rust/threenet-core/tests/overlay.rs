//! The screen space overlay renders on top of the 3D frame.

use threenet_core::camera::Camera;
use threenet_core::overlay::{Anchor, OverlayContent, OverlayElement, TextAlign};
use threenet_core::renderer::{Renderer, RendererConfig, ToneMapping};
use threenet_core::scene::Scene;
use threenet_core::texture::{Texture, TextureFormat};

const WIDTH: u32 = 320;
const HEIGHT: u32 = 200;

fn pixel(pixels: &[u8], x: u32, y: u32) -> [u8; 4] {
    let i = ((y * WIDTH + x) * 4) as usize;
    [pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]]
}

#[test]
fn panels_images_and_text_draw_over_the_scene() {
    let config = RendererConfig {
        width: WIDTH,
        height: HEIGHT,
        msaa_samples: 1,
        tone_mapping: ToneMapping::None,
        ..Default::default()
    };
    let Ok(mut renderer) = Renderer::new_offscreen(config) else {
        eprintln!("skipping GPU test: no adapter");
        return;
    };

    let mut scene = Scene::new();
    scene.environment.background = [0.0, 0.0, 0.0, 1.0];
    let camera = scene.create_node(None).unwrap();
    scene.node_mut(camera).unwrap().camera = Some(Camera::perspective(1.0, 0.1, 10.0));

    // Red panel, top left, with a white border and rounded corners.
    scene
        .overlay
        .add(OverlayElement {
            offset: [10.0, 10.0],
            size: [120.0, 60.0],
            color: [1.0, 0.0, 0.0, 1.0],
            border_color: [1.0, 1.0, 1.0, 1.0],
            border_width: 3.0,
            corner_radius: 12.0,
            ..OverlayElement::new(OverlayContent::Panel)
        })
        .unwrap();

    // Half transparent green panel bottom right.
    scene
        .overlay
        .add(OverlayElement {
            anchor: Anchor::BottomRight,
            offset: [-10.0, -10.0],
            size: [80.0, 40.0],
            color: [0.0, 1.0, 0.0, 0.5],
            ..OverlayElement::new(OverlayContent::Panel)
        })
        .unwrap();

    // A blue 2x2 texture drawn as an image in the top right.
    let texture = scene.add_texture(Texture::new(2, 2, TextureFormat::Rgba8UnormSrgb, [0, 0, 255, 255].repeat(4)).unwrap());
    scene
        .overlay
        .add(OverlayElement {
            anchor: Anchor::TopRight,
            offset: [-10.0, 10.0],
            size: [50.0, 50.0],
            ..OverlayElement::new(OverlayContent::Image { texture, uv: [0.0, 0.0, 1.0, 1.0] })
        })
        .unwrap();

    // White text centred in the viewport.
    scene
        .overlay
        .add(OverlayElement {
            anchor: Anchor::Center,
            size: [300.0, 40.0],
            color: [1.0, 1.0, 1.0, 1.0],
            ..OverlayElement::new(OverlayContent::Text {
                text: "Three.Net HUD 123".into(),
                font: 0,
                size: 28.0,
                align: TextAlign::Center,
                vertical_align: TextAlign::Center,
                wrap: false,
            })
        })
        .unwrap();

    renderer.render(&mut scene, Some(camera)).unwrap();
    let pixels = renderer.read_pixels().unwrap();

    if let Ok(dir) = std::env::var("THREENET_TEST_OUTPUT") {
        image::save_buffer(format!("{dir}/overlay.png"), &pixels, WIDTH, HEIGHT, image::ExtendedColorType::Rgba8).unwrap();
    }

    let red = pixel(&pixels, 70, 40);
    assert!(red[0] > 240 && red[1] < 20, "panel fill {red:?}");
    let border = pixel(&pixels, 11, 40);
    assert!(border[0] > 200 && border[1] > 200, "panel border {border:?}");
    let corner = pixel(&pixels, 11, 11);
    assert!(corner[0] < 40, "rounded corner stays clear {corner:?}");

    let green = pixel(&pixels, WIDTH - 50, HEIGHT - 30);
    assert!(green[1] > 150 && green[1] < 230 && green[0] < 20, "blended panel {green:?}");

    let blue = pixel(&pixels, WIDTH - 35, 35);
    assert!(blue[2] > 240 && blue[0] < 20, "image {blue:?}");

    // Text: some bright pixels in the centre band, background elsewhere.
    let band = (80..120).flat_map(|y| (30..290).map(move |x| (x, y)));
    let lit = band.filter(|(x, y)| pixel(&pixels, *x, *y)[0] > 200).count();
    assert!(lit > 150 && lit < 4000, "text coverage {lit}");
}
