//! KTX2 and Basis Universal: the test encodes a synthetic image with the Basis
//! encoder (ETC1S and UASTC), wraps the result in `.basis` and KTX2 containers
//! and checks that every path decodes back close to the source.

use basis_universal::encoding::{Compressor, CompressorParams};
use basis_universal::{BasisTextureFormat, Transcoder, TranscoderTextureFormat, TranscodeParameters};
use threenet_core::compressed::{CompressedData, CompressedFormat, Resolved, resolve};
use threenet_core::texture::Texture;

const SIZE: u32 = 64;

fn source_image() -> Vec<u8> {
    let mut pixels = Vec::with_capacity((SIZE * SIZE * 4) as usize);
    for y in 0..SIZE {
        for x in 0..SIZE {
            let checker = if ((x / 8) + (y / 8)) % 2 == 0 { 40 } else { 0 };
            pixels.extend_from_slice(&[(x * 4) as u8, (y * 4) as u8, 180 + checker, 255]);
        }
    }
    pixels
}

fn encode_basis(format: BasisTextureFormat) -> Vec<u8> {
    let source = source_image();
    let mut params = CompressorParams::new();
    params.set_basis_format(format);
    params.set_generate_mipmaps(true);
    params.source_image_mut(0).init(&source, SIZE, SIZE, 4);
    let mut compressor = Compressor::new(4);
    unsafe {
        assert!(compressor.init(&params));
        compressor.process().expect("basis encoding");
    }
    compressor.basis_file().to_vec()
}

fn psnr(a: &[u8], b: &[u8]) -> f64 {
    let mut error = 0f64;
    let mut count = 0f64;
    for (pa, pb) in a.chunks_exact(4).zip(b.chunks_exact(4)) {
        for c in 0..3 {
            let d = pa[c] as f64 - pb[c] as f64;
            error += d * d;
            count += 1.0;
        }
    }
    let mse = (error / count).max(1e-9);
    10.0 * (255.0f64 * 255.0 / mse).log10()
}

fn decode_to_rgba(data: &CompressedData, features: wgpu::Features) -> Vec<u8> {
    match resolve(data, SIZE, SIZE, features).unwrap() {
        Resolved::Rgba8(pixels) => pixels,
        Resolved::Blocks { format, levels } => {
            assert!(levels.len() > 1, "the mip chain should be kept");
            format.decode(&levels[0], SIZE, SIZE).unwrap()
        }
    }
}

// ---------------------------------------------------------------- .basis -> KTX2

fn read(data: &[u8], offset: usize, bytes: usize) -> usize {
    let mut value = 0usize;
    for i in 0..bytes {
        value |= (data[offset + i] as usize) << (8 * i);
    }
    value
}

struct SliceInfo {
    level: usize,
    alpha: bool,
    offset: usize,
    size: usize,
}

fn basis_slices(file: &[u8]) -> Vec<SliceInfo> {
    let total = read(file, 14, 3);
    let descs = read(file, 65, 4);
    (0..total)
        .map(|i| {
            let d = descs + i * 23;
            SliceInfo {
                level: read(file, d + 3, 1),
                alpha: read(file, d + 4, 1) & 1 != 0,
                offset: read(file, d + 13, 4),
                size: read(file, d + 17, 4),
            }
        })
        .collect()
}

/// Minimal KTX2 writer mirroring what `toktx` produces for Basis payloads.
fn basis_to_ktx2(file: &[u8], uastc: bool, zstd: bool) -> Vec<u8> {
    let slices = basis_slices(file);
    let levels = slices.iter().map(|s| s.level).max().unwrap() + 1;

    // Level payloads (RGB and optional alpha slice back to back for ETC1S).
    let mut level_payloads: Vec<Vec<u8>> = vec![Vec::new(); levels];
    let mut image_descs = Vec::new();
    for level in 0..levels {
        let rgb = slices.iter().find(|s| s.level == level && !s.alpha).unwrap();
        let alpha = slices.iter().find(|s| s.level == level && s.alpha);
        let payload = &mut level_payloads[level];
        let rgb_offset = payload.len();
        payload.extend_from_slice(&file[rgb.offset..rgb.offset + rgb.size]);
        let (alpha_offset, alpha_length) = match alpha {
            Some(a) => {
                let offset = payload.len();
                payload.extend_from_slice(&file[a.offset..a.offset + a.size]);
                (offset, a.size)
            }
            None => (0, 0),
        };
        for value in [0u32, rgb_offset as u32, rgb.size as u32, alpha_offset as u32, alpha_length as u32] {
            image_descs.extend_from_slice(&value.to_le_bytes());
        }
    }

    // BasisLZ global data for ETC1S.
    let mut sgd = Vec::new();
    if !uastc {
        // basisu_file_header: endpoints @39, selectors @48, tables @57.
        let endpoints = (read(file, 39, 2), read(file, 41, 4), read(file, 45, 3));
        let selectors = (read(file, 48, 2), read(file, 50, 4), read(file, 54, 3));
        let tables_offset = read(file, 57, 4);
        let tables_size = read(file, 61, 4);
        sgd.extend_from_slice(&(endpoints.0 as u16).to_le_bytes());
        sgd.extend_from_slice(&(selectors.0 as u16).to_le_bytes());
        sgd.extend_from_slice(&(endpoints.2 as u32).to_le_bytes());
        sgd.extend_from_slice(&(selectors.2 as u32).to_le_bytes());
        sgd.extend_from_slice(&(tables_size as u32).to_le_bytes());
        sgd.extend_from_slice(&0u32.to_le_bytes());
        sgd.extend_from_slice(&image_descs);
        sgd.extend_from_slice(&file[endpoints.1..endpoints.1 + endpoints.2]);
        sgd.extend_from_slice(&file[selectors.1..selectors.1 + selectors.2]);
        sgd.extend_from_slice(&file[tables_offset..tables_offset + tables_size]);
    }

    let stored: Vec<Vec<u8>> = level_payloads
        .iter()
        .map(|p| if zstd { ruzstd::encoding::compress_to_vec(&p[..], ruzstd::encoding::CompressionLevel::Fastest) } else { p.clone() })
        .collect();

    // Data format descriptor: one basic block with one sample.
    let color_model: u8 = if uastc { 166 } else { 163 };
    let mut dfd = Vec::new();
    dfd.extend_from_slice(&44u32.to_le_bytes()); // total size
    dfd.extend_from_slice(&0u32.to_le_bytes()); // vendor + type
    dfd.extend_from_slice(&2u16.to_le_bytes()); // version
    dfd.extend_from_slice(&40u16.to_le_bytes()); // block size (24 + 16)
    dfd.extend_from_slice(&[color_model, 1, 2, 0]); // model, BT709, sRGB, flags
    dfd.extend_from_slice(&[3, 3, 0, 0]); // texel block dimensions
    dfd.extend_from_slice(&[0; 8]); // bytes planes
    let channel: u8 = if uastc { 3 } else { 0 };
    dfd.extend_from_slice(&[0, 0, 63, channel]); // bit offset, length, channel
    dfd.extend_from_slice(&[0; 4]);
    dfd.extend_from_slice(&0u32.to_le_bytes());
    dfd.extend_from_slice(&u32::MAX.to_le_bytes());

    let level_index_size = levels * 24;
    let dfd_offset = 80 + level_index_size;
    let sgd_offset = dfd_offset + dfd.len();
    let mut data_offset = sgd_offset + sgd.len();

    let mut out = Vec::new();
    out.extend_from_slice(&[0xAB, b'K', b'T', b'X', b' ', b'2', b'0', 0xBB, 0x0D, 0x0A, 0x1A, 0x0A]);
    let supercompression: u32 = if uastc { if zstd { 2 } else { 0 } } else { 1 };
    for value in [0u32, 1, SIZE, SIZE, 0, 0, 1, levels as u32, supercompression, dfd_offset as u32, dfd.len() as u32, 0, 0] {
        out.extend_from_slice(&value.to_le_bytes());
    }
    out.extend_from_slice(&(if sgd.is_empty() { 0u64 } else { sgd_offset as u64 }).to_le_bytes());
    out.extend_from_slice(&(sgd.len() as u64).to_le_bytes());
    for level in 0..levels {
        out.extend_from_slice(&(data_offset as u64).to_le_bytes());
        out.extend_from_slice(&(stored[level].len() as u64).to_le_bytes());
        out.extend_from_slice(&(level_payloads[level].len() as u64).to_le_bytes());
        data_offset += stored[level].len();
    }
    out.extend_from_slice(&dfd);
    out.extend_from_slice(&sgd);
    for payload in &stored {
        out.extend_from_slice(payload);
    }
    out
}

fn compressed(texture: &Texture) -> &CompressedData {
    texture.compressed.as_ref().expect("texture should stay compressed until upload")
}

#[test]
fn basis_files_transcode_for_every_gpu_family() {
    let source = source_image();
    for (format, label, minimum_psnr) in [(BasisTextureFormat::ETC1S, "ETC1S", 22.0), (BasisTextureFormat::UASTC4x4, "UASTC", 30.0)] {
        let file = encode_basis(format);
        let texture = Texture::from_encoded_bytes(&file, true).unwrap();
        assert_eq!((texture.width, texture.height), (SIZE, SIZE));

        for features in [
            wgpu::Features::empty(),
            wgpu::Features::TEXTURE_COMPRESSION_BC,
            wgpu::Features::TEXTURE_COMPRESSION_ETC2,
            wgpu::Features::TEXTURE_COMPRESSION_ASTC,
        ] {
            let decoded = decode_to_rgba(compressed(&texture), features);
            let quality = psnr(&source, &decoded);
            assert!(quality > minimum_psnr, "{label} via {features:?}: {quality:.1} dB");
        }
    }
}

#[test]
fn ktx2_basis_payloads_match_the_basis_file() {
    let source = source_image();
    for (format, uastc, zstd) in [
        (BasisTextureFormat::ETC1S, false, false),
        (BasisTextureFormat::UASTC4x4, true, false),
        (BasisTextureFormat::UASTC4x4, true, true),
    ] {
        let file = encode_basis(format);
        let ktx2 = basis_to_ktx2(&file, uastc, zstd);
        let texture = Texture::from_encoded_bytes(&ktx2, true).unwrap();
        let decoded = decode_to_rgba(compressed(&texture), wgpu::Features::TEXTURE_COMPRESSION_BC);
        let quality = psnr(&source, &decoded);
        assert!(quality > 22.0, "KTX2 uastc={uastc} zstd={zstd}: {quality:.1} dB");
    }
}

#[test]
fn ktx2_block_and_raw_formats() {
    let source = source_image();

    // BC7 blocks produced by the Basis transcoder, stored in a plain KTX2.
    let file = encode_basis(BasisTextureFormat::UASTC4x4);
    basis_universal::transcoder_init();
    let mut transcoder = Transcoder::new();
    transcoder.prepare_transcoding(&file).unwrap();
    let blocks = transcoder
        .transcode_image_level(&file, TranscoderTextureFormat::BC7_RGBA, TranscodeParameters {
            image_index: 0,
            level_index: 0,
            decode_flags: None,
            output_row_pitch_in_blocks_or_pixels: None,
            output_rows_in_pixels: None,
        })
        .unwrap();
    transcoder.end_transcoding();

    let wrap = |vk_format: u32, payload: &[u8], zstd: bool| -> Vec<u8> {
        let stored = if zstd {
            ruzstd::encoding::compress_to_vec(payload, ruzstd::encoding::CompressionLevel::Fastest)
        } else {
            payload.to_vec()
        };
        let dfd: Vec<u8> = {
            let mut d = Vec::new();
            d.extend_from_slice(&28u32.to_le_bytes());
            d.extend_from_slice(&[0u8; 24]);
            d
        };
        let dfd_offset = 80 + 24;
        let data_offset = dfd_offset + dfd.len();
        let mut out = vec![0xAB, b'K', b'T', b'X', b' ', b'2', b'0', 0xBB, 0x0D, 0x0A, 0x1A, 0x0A];
        for value in [vk_format, 1, SIZE, SIZE, 0, 0, 1, 1, if zstd { 2 } else { 0 }, dfd_offset as u32, dfd.len() as u32, 0, 0] {
            out.extend_from_slice(&value.to_le_bytes());
        }
        out.extend_from_slice(&0u64.to_le_bytes());
        out.extend_from_slice(&0u64.to_le_bytes());
        out.extend_from_slice(&(data_offset as u64).to_le_bytes());
        out.extend_from_slice(&(stored.len() as u64).to_le_bytes());
        out.extend_from_slice(&(payload.len() as u64).to_le_bytes());
        out.extend_from_slice(&dfd);
        out.extend_from_slice(&stored);
        out
    };

    // BC7 without GPU support: decoded on the CPU.
    let bc7 = Texture::from_encoded_bytes(&wrap(146, &blocks, false), true).unwrap();
    match compressed(&bc7) {
        CompressedData::Blocks { format, .. } => assert_eq!(*format, CompressedFormat::Bc7),
        other => panic!("expected BC7 blocks, got {other:?}"),
    }
    match resolve(compressed(&bc7), SIZE, SIZE, wgpu::Features::empty()).unwrap() {
        Resolved::Rgba8(pixels) => assert!(psnr(&source, &pixels) > 30.0),
        Resolved::Blocks { .. } => panic!("no BC support: must be decoded"),
    }
    // With GPU support the blocks pass straight through.
    assert!(matches!(
        resolve(compressed(&bc7), SIZE, SIZE, wgpu::Features::TEXTURE_COMPRESSION_BC).unwrap(),
        Resolved::Blocks { format: CompressedFormat::Bc7, .. }
    ));

    // Raw RGBA8 with zstd supercompression is exact.
    let raw = Texture::from_encoded_bytes(&wrap(43, &source, true), true).unwrap();
    assert!(raw.compressed.is_none());
    assert_eq!(raw.pixels, source);
}

/// Renders a textured plane filling the view with the raw image and with the
/// Basis encoded copy; the two frames must look alike on whatever GPU runs.
#[test]
fn basis_textures_render_like_the_source() {
    use threenet_core::camera::Camera;
    use threenet_core::geometry::primitives;
    use threenet_core::material::Material;
    use threenet_core::math::{Vec3, Vec4};
    use threenet_core::renderer::{Renderer, RendererConfig, ToneMapping};
    use threenet_core::scene::Scene;
    use threenet_core::texture::TextureFormat;

    let config = RendererConfig {
        width: 96,
        height: 96,
        msaa_samples: 1,
        tone_mapping: ToneMapping::None,
        ..Default::default()
    };
    let Ok(mut renderer) = Renderer::new_offscreen(config) else {
        eprintln!("skipping GPU test: no adapter");
        return;
    };

    let basis = encode_basis(BasisTextureFormat::UASTC4x4);
    let mut frames = Vec::new();
    for compressed in [false, true] {
        let mut scene = Scene::new();
        scene.environment.background = [0.0, 0.0, 0.0, 1.0];
        let texture = if compressed {
            Texture::from_encoded_bytes(&basis, true).unwrap()
        } else {
            Texture::new(SIZE, SIZE, TextureFormat::Rgba8UnormSrgb, source_image()).unwrap()
        };
        let texture = scene.add_texture(texture);
        let mut material = Material::basic(Vec4::ONE);
        material.textures.base_color = Some(texture);
        let material = scene.add_material(material);
        let plane = scene.add_geometry(primitives::plane(2.0, 2.0, 1, 1));
        let node = scene.add_mesh(None, plane, material).unwrap();
        scene.node_mut(node).unwrap().transform.translation = Vec3::new(0.0, 0.0, -1.0);
        let camera = scene.create_node(None).unwrap();
        scene.node_mut(camera).unwrap().camera = Some(Camera::perspective(1.2, 0.1, 10.0));
        scene.mark_dirty(scene.root());
        renderer.render(&mut scene, Some(camera)).unwrap();
        frames.push(renderer.read_pixels().unwrap());
    }
    let quality = psnr(&frames[0], &frames[1]);
    assert!(quality > 28.0, "Basis render differs from the source: {quality:.1} dB");
}

/// Regenerates the small compressed files used by the .NET tests and samples:
/// `cargo test --test compressed -- --ignored`.
#[test]
#[ignore]
fn write_sample_files() {
    let dir = format!("{}/tests/assets", env!("CARGO_MANIFEST_DIR"));
    let etc1s = encode_basis(BasisTextureFormat::ETC1S);
    std::fs::write(format!("{dir}/checker-etc1s.basis"), &etc1s).unwrap();
    let uastc = encode_basis(BasisTextureFormat::UASTC4x4);
    std::fs::write(format!("{dir}/checker-uastc.ktx2"), basis_to_ktx2(&uastc, true, true)).unwrap();
}
