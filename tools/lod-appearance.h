#pragma once

#include <algorithm>
#include <array>
#include <cmath>
#include <limits>
#include <unordered_map>
#include <unordered_set>
#include <vector>

namespace lodstudio
{
struct Vec3
{
	float x, y, z;
	float axis(int i) const { return i == 0 ? x : i == 1 ? y : z; }
};

inline Vec3 xyz(const Attr& a) { return {a.f[0], a.f[1], a.f[2]}; }
inline Vec3 operator+(Vec3 a, Vec3 b) { return {a.x + b.x, a.y + b.y, a.z + b.z}; }
inline Vec3 operator-(Vec3 a, Vec3 b) { return {a.x - b.x, a.y - b.y, a.z - b.z}; }
inline Vec3 operator*(Vec3 a, float b) { return {a.x * b, a.y * b, a.z * b}; }
inline float dot(Vec3 a, Vec3 b) { return a.x * b.x + a.y * b.y + a.z * b.z; }
inline Vec3 cross(Vec3 a, Vec3 b) { return {a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x}; }
inline Vec3 unit(Vec3 a)
{
	float length = std::sqrt(dot(a, a));
	return length > 0 ? a * (1.f / length) : Vec3{};
}

struct Bounds
{
	Vec3 low = {INFINITY, INFINITY, INFINITY};
	Vec3 high = {-INFINITY, -INFINITY, -INFINITY};

	void include(Vec3 p)
	{
		low = {std::min(low.x, p.x), std::min(low.y, p.y), std::min(low.z, p.z)};
		high = {std::max(high.x, p.x), std::max(high.y, p.y), std::max(high.z, p.z)};
	}

	float extent() const
	{
		Vec3 span = high - low;
		return std::max(span.x, std::max(span.y, span.z));
	}

	float distanceSquared(Vec3 p) const
	{
		Vec3 delta = {
			std::max(0.f, std::max(low.x - p.x, p.x - high.x)),
			std::max(0.f, std::max(low.y - p.y, p.y - high.y)),
			std::max(0.f, std::max(low.z - p.z, p.z - high.z)),
		};
		return dot(delta, delta);
	}
};

// Retain a position-space and an attribute-space frame for each surface triangle.
struct Face
{
	std::array<unsigned int, 3> key;
	unsigned int vertices[3];
	Vec3 position[3];
	Bounds bounds;
	Vec3 geometricNormal;
	Vec3 tangent;
	Vec3 bitangent;
	float areaSquared;
	int uvOrientation;
};

struct BoneWeights
{
	unsigned int indices[12] = {};
	float weights[12] = {};
	size_t count = 0;

	void add(unsigned int index, float weight)
	{
		if (weight <= 0)
			return;
		size_t i = 0;
		while (i < count && indices[i] != index)
			++i;
		if (i == count)
		{
			indices[count] = index;
			++count;
		}
		weights[i] += weight;
	}

	float sharedWeight(const BoneWeights& other) const
	{
		float shared = 0;
		for (size_t i = 0; i < count; ++i)
			for (size_t j = 0; j < other.count; ++j)
				if (indices[i] == other.indices[j])
					shared += std::min(weights[i], other.weights[j]);
		return shared;
	}
};

struct Geometry
{
	const std::vector<Attr>& positions;
	const std::vector<float>& attributes;
	const std::vector<Attr>& joints;
	const std::vector<Attr>& weights;
	const std::vector<unsigned int>& localToSource;
	const Stream* colorStream;
	const Stream* tangentStream;
	const std::vector<const Stream*>& additionalUvs;
	size_t attributeStride;
	bool hasNormals;
	bool hasUv;
	bool hasNormalMap;
	Vec3 origin;
	float inverseScale;
	float densityTolerance;
	float uvProjectionRadiusSquared;
	float detailTolerance;
	bool coarseCorrespondence;
	int fidelityMode;
	bool shadowMode;

	Vec3 position(unsigned int i) const { return (xyz(positions[i]) - origin) * inverseScale; }
	const float* vertexAttributes(unsigned int i) const { return &attributes[i * attributeStride]; }
	std::array<float, 8> interpolate(const Face& face, Vec3 barycentric) const
	{
		const float* a = vertexAttributes(face.vertices[0]);
		const float* b = vertexAttributes(face.vertices[1]);
		const float* c = vertexAttributes(face.vertices[2]);
		std::array<float, 8> result;
		for (size_t k = 0; k < result.size(); ++k)
			result[k] = a[k] * barycentric.x + b[k] * barycentric.y + c[k] * barycentric.z;
		return result;
	}

	Attr interpolateStream(const Stream* stream, const Face& face, Vec3 barycentric) const
	{
		Attr result = {};
		if (stream)
			for (int i = 0; i < 3; ++i)
				for (int k = 0; k < 4; ++k)
					result.f[k] += stream->data[localToSource[face.vertices[i]]].f[k] * barycentric.axis(i);
		return result;
	}

	BoneWeights interpolateSkin(const Face& face, Vec3 barycentric) const
	{
		BoneWeights result;
		if (!joints.empty())
			for (int i = 0; i < 3; ++i)
			{
				unsigned int vertex = face.vertices[i];
				for (int j = 0; j < 4; ++j)
					result.add(unsigned(joints[vertex].f[j]), weights[vertex].f[j] * barycentric.axis(i));
			}
		return result;
	}
};

inline Face buildFace(const Geometry& geometry, const unsigned int* indices)
{
	Face result = {};
	for (int i = 0; i < 3; ++i)
	{
		result.vertices[i] = indices[i];
		result.position[i] = geometry.position(indices[i]);
		result.bounds.include(result.position[i]);
	}
	// Canonical cyclic order preserves winding as part of face identity.
	int first = int(std::min_element(indices, indices + 3) - indices);
	for (int i = 0; i < 3; ++i)
		result.key[i] = indices[(first + i) % 3];
	Vec3 e1 = result.position[1] - result.position[0];
	Vec3 e2 = result.position[2] - result.position[0];
	Vec3 n = cross(e1, e2);
	result.areaSquared = dot(n, n);
	result.geometricNormal = unit(n);
	if (geometry.hasUv)
	{
		const float* a = geometry.vertexAttributes(indices[0]) + 6;
		const float* b = geometry.vertexAttributes(indices[1]) + 6;
		const float* c = geometry.vertexAttributes(indices[2]) + 6;
		float u1 = b[0] - a[0], v1 = b[1] - a[1];
		float u2 = c[0] - a[0], v2 = c[1] - a[1];
		float det = u1 * v2 - u2 * v1;
		// Use a relative test so that tiny but well-formed texture charts are valid.
		float scale = (u1 * u1 + v1 * v1) * (u2 * u2 + v2 * v2);
		if (det * det > scale * 1e-8f && std::abs(det) > 1e-16f)
		{
			result.uvOrientation = det > 0 ? 1 : -1;
			result.tangent = unit((e1 * v2 - e2 * v1) * float(result.uvOrientation));
			result.bitangent = unit((e2 * u1 - e1 * u2) * float(result.uvOrientation));
		}
	}
	return result;
}

inline Vec3 interpolatePosition(const Face& face, Vec3 barycentric)
{
	return face.position[0] * barycentric.x + face.position[1] * barycentric.y + face.position[2] * barycentric.z;
}

inline Vec3 closestBarycentric(const Face& face, Vec3 point)
{
	Vec3 a = face.position[0], b = face.position[1], c = face.position[2];
	Vec3 e1 = b - a, e2 = c - a;
	// The cross-product form stays usable on more slender triangles than a Gram determinant.
	Vec3 n = cross(e1, e2);
	if (face.areaSquared > 1e-24f)
	{
		float y = dot(cross(point - a, e2), n) / face.areaSquared;
		float z = dot(cross(e1, point - a), n) / face.areaSquared;
		if (y >= 0 && z >= 0 && y + z <= 1)
			return {1 - y - z, y, z};
	}

	const Vec3 corners[] = {{1, 0, 0}, {0, 1, 0}, {0, 0, 1}};
	Vec3 best = corners[0];
	float bestDistance = INFINITY;
	for (int i = 0; i < 3; ++i)
	{
		int j = (i + 1) % 3;
		Vec3 start = face.position[i], edge = face.position[j] - start;
		float size = dot(edge, edge);
		float t = size > 0 ? std::max(0.f, std::min(1.f, dot(point - start, edge) / size)) : 0;
		Vec3 delta = start + edge * t - point;
		float distance = dot(delta, delta);
		if (distance < bestDistance)
		{
			bestDistance = distance;
			best = corners[i] * (1 - t) + corners[j] * t;
		}
	}
	return best;
}

struct Sample
{
	Vec3 point;
	Vec3 normal;
	std::array<float, 8> attributes;
	BoneWeights skin;
	Attr tangent;
	float alpha;
	Vec3 barycentric;
};

inline Vec3 shadingNormal(const Geometry& geometry, const Face& face, const std::array<float, 8>& attributes)
{
	if (!geometry.hasNormals)
		return face.geometricNormal;
	Vec3 normal = unit({attributes[0], attributes[1], attributes[2]});
	return dot(normal, normal) > 0 ? normal : face.geometricNormal;
}

inline bool tangentMatches(const Face& a, const Face& b, Vec3 normalA, Vec3 normalB, float minimumAlignment)
{
	Vec3 ta = unit(a.tangent - normalA * dot(a.tangent, normalA));
	Vec3 tb = unit(b.tangent - normalB * dot(b.tangent, normalB));
	float handA = dot(cross(normalA, ta), a.bitangent);
	float handB = dot(cross(normalB, tb), b.bitangent);
	// Front/back handedness matters on thin, mirrored and double-sided meshes.
	return dot(ta, tb) >= minimumAlignment && handA * handB > 0.f;
}

inline int secondaryUvOrientation(const Geometry& geometry, const Stream* stream, const Face& face)
{
	const float* a = stream->data[geometry.localToSource[face.vertices[0]]].f;
	const float* b = stream->data[geometry.localToSource[face.vertices[1]]].f;
	const float* c = stream->data[geometry.localToSource[face.vertices[2]]].f;
	float u1 = b[0] - a[0], v1 = b[1] - a[1], u2 = c[0] - a[0], v2 = c[1] - a[1];
	float det = u1 * v2 - u2 * v1;
	float scale = (u1 * u1 + v1 * v1) * (u2 * u2 + v2 * v2);
	return det * det <= scale * 1e-8f || std::abs(det) <= 1e-16f ? 0 : det > 0 ? 1 : -1;
}

inline bool skinMatches(const Geometry& geometry, const Sample& sample, const Face& to, Vec3 barycentric)
{
	float density = geometry.densityTolerance;
	if (!geometry.joints.empty())
	{
		BoneWeights other = geometry.interpolateSkin(to, barycentric);
		float total = 0;
		for (size_t i = 0; i < sample.skin.count; ++i)
			total += sample.skin.weights[i];
		for (size_t i = 0; i < other.count; ++i)
			total += other.weights[i];
		if (total - 2 * sample.skin.sharedWeight(other) > 0.22f + 0.065f * std::min(density, 2.65f))
			return false;
	}
	return true;
}

inline bool additionalUvsMatch(const Geometry& geometry, const Face& from, const Sample& sample, const Face& to, Vec3 barycentric)
{
	for (const Stream* stream : geometry.additionalUvs)
	{
		Attr before = geometry.interpolateStream(stream, from, sample.barycentric);
		Attr after = geometry.interpolateStream(stream, to, barycentric);
		float tolerance = std::min(0.018f, 0.0075f + 0.003f * geometry.densityTolerance);
		if (geometry.fidelityMode == 0)
			tolerance *= 0.85f;
		if (std::abs(before.f[0] - after.f[0]) > tolerance || std::abs(before.f[1] - after.f[1]) > tolerance ||
		    secondaryUvOrientation(geometry, stream, from) != secondaryUvOrientation(geometry, stream, to))
			return false;
	}

	return true;
}

inline bool appearanceMatches(const Geometry& geometry, const Face& from, const Sample& sample, const Face& to, Vec3 barycentric)
{
	if (geometry.shadowMode)
		return skinMatches(geometry, sample, to, barycentric);

	auto attrs = geometry.interpolate(to, barycentric);
	Vec3 normal = shadingNormal(geometry, to, attrs);
	if (geometry.coarseCorrespondence)
	{
		if (dot(sample.normal, normal) < (geometry.hasNormalMap ? 0.55f : 0.28f) ||
		    (geometry.hasNormalMap && from.uvOrientation != to.uvOrientation))
			return false;
		return additionalUvsMatch(geometry, from, sample, to, barycentric) &&
			skinMatches(geometry, sample, to, barycentric);
	}

	// Facing, degeneracy and tangent handedness stay hard limits.
	float density = geometry.densityTolerance;
	float detail = geometry.detailTolerance;
	float normalAlignment = std::max((geometry.hasNormalMap ? 0.68f : 0.61f) - 0.12f * detail,
		(geometry.hasNormalMap ? 0.94f : 0.90f) - 0.065f * density - 0.18f * detail);
	if (geometry.fidelityMode == 0)
		normalAlignment += (1.f - normalAlignment) * 0.2f;
	if (dot(sample.normal, normal) < normalAlignment)
		return false;

	// Preserve authored normals that deliberately point away from a geometric face.
	float sideA = dot(sample.normal, from.geometricNormal), sideB = dot(normal, to.geometricNormal);
	if ((sideA < -0.1f && sideB > 0.1f) || (sideB < -0.1f && sideA > 0.1f))
		return false;

	if (geometry.colorStream)
	{
		for (int k = 3; k < 6; ++k)
			if (std::abs(attrs[k] - sample.attributes[k]) > 0.035f)
				return false;
		if (std::abs(geometry.interpolateStream(geometry.colorStream, to, barycentric).f[3] - sample.alpha) > 0.035f)
			return false;
	}

	if (geometry.tangentStream)
	{
		Attr tangent = geometry.interpolateStream(geometry.tangentStream, to, barycentric);
		if (tangent.f[3] * sample.tangent.f[3] <= 0 ||
		    dot(unit(xyz(tangent)), unit(xyz(sample.tangent))) < 0.9f)
			return false;
	}

	if (geometry.hasUv)
	{
		float uvTolerance = std::min(0.032f, 0.0075f + 0.004f * density) + 0.028f * detail;
		if (geometry.fidelityMode == 0)
			uvTolerance *= 0.85f;
		if (std::abs(attrs[6] - sample.attributes[6]) > uvTolerance ||
		    std::abs(attrs[7] - sample.attributes[7]) > uvTolerance ||
		    (from.uvOrientation == 0) != (to.uvOrientation == 0) ||
		    (geometry.fidelityMode == 2 && from.uvOrientation != to.uvOrientation))
			return false;
		if (from.uvOrientation != 0 && !tangentMatches(from, to, sample.normal, normal,
		    std::max(0.57f, (geometry.hasNormalMap ? 0.82f : 0.75f) - 0.05f * density) - 0.12f * detail))
			return false;
	}

	return additionalUvsMatch(geometry, from, sample, to, barycentric) && skinMatches(geometry, sample, to, barycentric);
}

struct FaceKeyHash
{
	size_t operator()(const std::array<unsigned int, 3>& key) const
	{
		return (size_t(key[0]) * 73856093u) ^ (size_t(key[1]) * 19349663u) ^ (size_t(key[2]) * 83492791u);
	}
};

class Surface
{
	struct Node
	{
		Bounds bounds;
		size_t first, last;
		size_t right; // zero for a leaf, left child follows the parent
	};

public:
	std::vector<Face> faces;
	std::unordered_set<std::array<unsigned int, 3>, FaceKeyHash> keys;

	Surface(const Geometry& geometry, const std::vector<unsigned int>& indices)
	{
		faces.reserve(indices.size() / 3);
		keys.reserve(indices.size() / 3);
		for (size_t i = 0; i < indices.size(); i += 3)
		{
			Face face = buildFace(geometry, &indices[i]);
			keys.insert(face.key);
			faces.push_back(face);
		}
		if (!faces.empty())
			build(0, faces.size());
	}

	bool matches(const Geometry& geometry, const Face& from, const Sample& sample, float maxDistanceSquared) const
	{
		return !nodes.empty() && matchNode(0, geometry, from, sample, maxDistanceSquared);
	}

	float distanceSquared(Vec3 point, const Face** closest = nullptr) const
	{
		return nodes.empty() ? INFINITY : nearest(0, point, INFINITY, closest);
	}

private:
	std::vector<Node> nodes;

	size_t build(size_t first, size_t last)
	{
		size_t index = nodes.size();
		Bounds bounds, centers;
		for (size_t i = first; i < last; ++i)
		{
			bounds.include(faces[i].bounds.low);
			bounds.include(faces[i].bounds.high);
			centers.include((faces[i].bounds.low + faces[i].bounds.high) * 0.5f);
		}
		nodes.push_back({bounds, first, last, 0});
		if (last - first > 8)
		{
			Vec3 span = centers.high - centers.low;
			int axis = (span.x >= span.y && span.x >= span.z) ? 0 : (span.y >= span.z ? 1 : 2);
			size_t middle = first + (last - first) / 2;
			std::nth_element(faces.begin() + first, faces.begin() + middle, faces.begin() + last,
				[axis](const Face& a, const Face& b) {
					return (a.bounds.low + a.bounds.high).axis(axis) < (b.bounds.low + b.bounds.high).axis(axis);
				});
			build(first, middle);
			size_t right = build(middle, last);
			nodes[index].right = right;
		}
		return index;
	}

	float nearest(size_t index, Vec3 point, float best, const Face** closest) const
	{
		const Node& node = nodes[index];
		if (node.bounds.distanceSquared(point) >= best)
			return best;
		if (node.right)
		{
			size_t first = index + 1, second = node.right;
			if (nodes[second].bounds.distanceSquared(point) < nodes[first].bounds.distanceSquared(point))
				std::swap(first, second);
			best = nearest(first, point, best, closest);
			return nearest(second, point, best, closest);
		}
		for (size_t i = node.first; i < node.last; ++i)
		{
			const Face& face = faces[i];
			if (face.bounds.distanceSquared(point) < best)
			{
				Vec3 delta = interpolatePosition(face, closestBarycentric(face, point)) - point;
				float distance = dot(delta, delta);
				if (distance < best)
				{
					best = distance;
					if (closest)
						*closest = &face;
				}
			}
		}
		return best;
	}

	bool matchNode(size_t index, const Geometry& geometry, const Face& from, const Sample& sample, float maxDistanceSquared) const
	{
		const Node& node = nodes[index];
		if (node.bounds.distanceSquared(sample.point) > maxDistanceSquared)
			return false;
		if (node.right)
			return matchNode(index + 1, geometry, from, sample, maxDistanceSquared) ||
			       matchNode(node.right, geometry, from, sample, maxDistanceSquared);
		for (size_t i = node.first; i < node.last; ++i)
		{
			const Face& face = faces[i];
			float facing = geometry.shadowMode ? 0.65f : 0.65f - 0.19f * geometry.detailTolerance;
			if (face.areaSquared <= 1e-24f || face.bounds.distanceSquared(sample.point) > maxDistanceSquared ||
			    dot(from.geometricNormal, face.geometricNormal) < facing)
				continue;
			Vec3 barycentric = closestBarycentric(face, sample.point);
			Vec3 delta = interpolatePosition(face, barycentric) - sample.point;
			if (dot(delta, delta) <= maxDistanceSquared && appearanceMatches(geometry, from, sample, face, barycentric))
				return true;

			// Also test the same point in the texture chart. A small displacement
			// along a curved surface can be cheaper than distorting its texture.
			if (geometry.uvProjectionRadiusSquared == 0 || face.uvOrientation == 0 ||
			    face.uvOrientation != from.uvOrientation)
				continue;
			const float* a = geometry.vertexAttributes(face.vertices[0]) + 6;
			const float* b = geometry.vertexAttributes(face.vertices[1]) + 6;
			const float* c = geometry.vertexAttributes(face.vertices[2]) + 6;
			float bu = b[0] - a[0], bv = b[1] - a[1];
			float cu = c[0] - a[0], cv = c[1] - a[1];
			float u = sample.attributes[6] - a[0], v = sample.attributes[7] - a[1];
			float det = bu * cv - bv * cu;
			float y = (u * cv - v * cu) / det, z = (bu * v - bv * u) / det;
			if (!(y >= 0 && z >= 0 && y + z <= 1))
				continue;
			barycentric = {1 - y - z, y, z};
			delta = interpolatePosition(face, barycentric) - sample.point;
			if (dot(delta, delta) <= geometry.uvProjectionRadiusSquared &&
			    appearanceMatches(geometry, from, sample, face, barycentric))
				return true;
		}
		return false;
	}
};

struct SurfaceDeviation
{
	unsigned int maxPpm;
	unsigned int meanPpm;
	size_t sampleCount;
};

inline SurfaceDeviation measureSurfaceDeviation(Geometry geometry, const std::vector<unsigned int>& sourceIndices,
	const std::vector<unsigned int>& reducedIndices)
{
	if (sourceIndices.empty() || reducedIndices.empty())
		return {};

	geometry.hasUv = false;
	const Surface original(geometry, sourceIndices), reduced(geometry, reducedIndices);
	float maximumSquared = 0;
	double sum = 0;
	auto measure = [&](const Surface& from, const Surface& to) {
		std::vector<float> cornerDistances(geometry.positions.size(), -1.f);
		for (const Face& face : from.faces)
		{
			if (to.keys.count(face.key))
				continue;
			float distances[4] = {to.distanceSquared(interpolatePosition(face, {1.f / 3, 1.f / 3, 1.f / 3}))};
			for (int i = 0; i < 3; ++i)
			{
				float& cached = cornerDistances[face.vertices[i]];
				if (cached < 0)
					cached = to.distanceSquared(face.position[i]);
				distances[i + 1] = cached;
			}
			for (float distance : distances)
			{
				maximumSquared = std::max(maximumSquared, distance);
				sum += std::sqrt(double(distance));
			}
		}
	};
	measure(original, reduced);
	measure(reduced, original);
	// The same four samples on identical triangles contribute exact zeros.
	size_t count = (original.faces.size() + reduced.faces.size()) * 4;
	auto ppm = [](double distance) {
		return unsigned(std::min(double(std::numeric_limits<unsigned int>::max()),
			std::round(distance * 1000000.0)));
	};
	return {ppm(std::sqrt(double(maximumSquared))), ppm(sum / count), count};
}

inline bool compareSamples(const Geometry& sourceGeometry, const Surface& from,
	const Geometry& targetGeometry, const Surface& to, float maxDistanceSquared,
	std::vector<unsigned char>& locks, bool checkNewFaces, const std::vector<unsigned char>* changed = nullptr,
	const Geometry* relaxedGeometry = nullptr, float relaxedAreaFraction = 0,
	const Geometry* coarseGeometry = nullptr, float coarseAreaFraction = 0)
{
	// Centroid, near each corner, and just inside each edge. An unchanged triangle is exact.
	const Vec3 probes[] = {
		{1.f/3, 1.f/3, 1.f/3}, {0.8f, 0.1f, 0.1f}, {0.1f, 0.8f, 0.1f}, {0.1f, 0.1f, 0.8f},
		{0.475f, 0.475f, 0.05f}, {0.475f, 0.05f, 0.475f}, {0.05f, 0.475f, 0.475f},
	};
	bool accepted = true;
	double totalArea = 0, relaxedArea = 0, coarseArea = 0;
	std::vector<unsigned int> relaxedVertices;
	for (const Face& face : from.faces)
	{
		float area = (relaxedGeometry || coarseGeometry) ? std::sqrt(face.areaSquared) : 0;
		totalArea += area;
		bool exact = !changed || (!(*changed)[face.vertices[0]] &&
		             !(*changed)[face.vertices[1]] && !(*changed)[face.vertices[2]]);
		if (exact && to.keys.count(face.key))
			continue;
		if (face.areaSquared <= 1e-24f)
		{
			if (checkNewFaces)
			{
				accepted = false;
				for (unsigned int vertex : face.vertices)
					locks[vertex] = meshopt_SimplifyVertex_Lock;
			}
			continue;
		}
		int relaxedProbes = 0, coarseProbes = 0;
		for (Vec3 probe : probes)
		{
			Sample sample = {};
			sample.point = interpolatePosition(face, probe);
			sample.barycentric = probe;
			sample.skin = sourceGeometry.interpolateSkin(face, probe);
			if (!sourceGeometry.shadowMode)
			{
				sample.attributes = sourceGeometry.interpolate(face, probe);
				sample.normal = shadingNormal(sourceGeometry, face, sample.attributes);
				sample.tangent = sourceGeometry.interpolateStream(sourceGeometry.tangentStream, face, probe);
				sample.alpha = sourceGeometry.interpolateStream(sourceGeometry.colorStream, face, probe).f[3];
			}
			if (!to.matches(targetGeometry, face, sample, maxDistanceSquared))
			{
				if (relaxedGeometry && to.matches(*relaxedGeometry, face, sample, maxDistanceSquared))
				{
					++relaxedProbes;
					continue;
				}
				if (coarseGeometry && to.matches(*coarseGeometry, face, sample, maxDistanceSquared))
				{
					++coarseProbes;
					continue;
				}
				for (unsigned int vertex : face.vertices)
					locks[vertex] = meshopt_SimplifyVertex_Lock;
				if (checkNewFaces && targetGeometry.fidelityMode == 2 && !targetGeometry.shadowMode)
				{
					// A rejected new face has already lost some original
					// vertices. Keep the source triangle beneath the failed
					// probe available on the next proposal.
					const Face* closest = nullptr;
					to.distanceSquared(sample.point, &closest);
					if (closest)
						for (unsigned int vertex : closest->vertices)
							locks[vertex] = meshopt_SimplifyVertex_Lock;
				}
				accepted = false;
				break;
			}
		}
		if (relaxedProbes + coarseProbes > 0)
		{
			relaxedArea += area * (relaxedProbes + coarseProbes) / (sizeof(probes) / sizeof(probes[0]));
			coarseArea += area * coarseProbes / (sizeof(probes) / sizeof(probes[0]));
			relaxedVertices.insert(relaxedVertices.end(), face.vertices, face.vertices + 3);
		}
	}
	if (relaxedArea > totalArea * relaxedAreaFraction || coarseArea > totalArea * coarseAreaFraction)
	{
		for (unsigned int vertex : relaxedVertices)
			locks[vertex] = meshopt_SimplifyVertex_Lock;
		return false;
	}
	return accepted;
}

struct ValidationResult
{
	std::vector<unsigned int> indices;
	// Only populated when a moving-vertex candidate was accepted.
	std::vector<Attr> updatedPositions;
	std::vector<float> updatedAttributes;
	std::vector<unsigned char> updatedVertices;
	size_t trianglesSavedByUpdate = 0;
	bool triedUpdate = false;
	bool retried = false;
	bool keptOriginal = false;
};

inline bool validateVertexUpdates(const Geometry& original, const std::vector<unsigned int>& indices,
	const std::vector<unsigned char>& locks, std::vector<Attr>& positions,
	std::vector<float>& attributes, std::vector<unsigned char>& changed, float maxMovement)
{
	bool moved = false;
	std::vector<unsigned char> visited(positions.size());
	for (unsigned int i : indices)
	{
		if (visited[i])
			continue;
		visited[i] = 1;
		// Restore anchors before building the candidate surface, including every UV/normal wedge.
		if (locks[i])
		{
			positions[i] = original.positions[i];
			std::copy(original.vertexAttributes(i), original.vertexAttributes(i) + original.attributeStride,
				attributes.begin() + i * original.attributeStride);
			continue;
		}

		Vec3 delta = (xyz(positions[i]) - xyz(original.positions[i])) * original.inverseScale;
		float displacement = dot(delta, delta);
		if (!std::isfinite(displacement))
			return false;

		float* values = &attributes[i * original.attributeStride];
		const float* before = original.vertexAttributes(i);
		if (displacement > maxMovement * maxMovement)
		{
			// Clamp the proposal before validation instead of discarding a region on one outlier.
			float fraction = 0.9999f * maxMovement / std::sqrt(displacement);
			for (int k = 0; k < 3; ++k)
				positions[i].f[k] = original.positions[i].f[k] +
					(positions[i].f[k] - original.positions[i].f[k]) * fraction;
			for (size_t k = 0; k < original.attributeStride; ++k)
				values[k] = before[k] + (values[k] - before[k]) * fraction;
			delta = (xyz(positions[i]) - xyz(original.positions[i])) * original.inverseScale;
			displacement = dot(delta, delta);
			if (displacement > maxMovement * maxMovement)
			{
				positions[i] = original.positions[i];
				std::copy(before, before + original.attributeStride, values);
				continue;
			}
		}
		if (original.hasNormals)
		{
			Vec3 normal = unit({values[0], values[1], values[2]});
			if (!(dot(normal, normal) > 0.f))
				return false;
			if (!std::equal(values, values + 3, before))
			{
				values[0] = normal.x;
				values[1] = normal.y;
				values[2] = normal.z;
			}
		}
		if (original.hasUv && (!std::isfinite(values[6]) || !std::isfinite(values[7])))
			return false;

		changed[i] = displacement > 0 ||
			!std::equal(values, values + original.attributeStride, before);
		moved |= displacement > 0;
	}
	return moved;
}

// A rejected budget is the lower bound. Look for a smaller valid step from
// the current accepted mesh, measured in whole triangles.
template <typename Propose>
inline bool bisectTarget(size_t rejected, size_t accepted, int trials, const Propose& propose)
{
	size_t low = rejected / 3, high = accepted / 3;
	for (int trial = 0; trial < trials && high > low + 1; ++trial)
	{
		size_t middle = low + (high - low) / 2;
		if (propose(middle * 3))
			return true;
		low = middle;
	}
	return false;
}

inline void tryVertexUpdates(ValidationResult& result, const std::vector<unsigned int>& source,
	const Geometry& geometry, const Surface& original,
	const float* weights, std::vector<unsigned char> locks, size_t target, size_t minimum,
	float error, unsigned int options)
{
	const size_t fixedCount = result.indices.size();
	if (geometry.shadowMode || geometry.colorStream || geometry.tangentStream ||
	    !geometry.additionalUvs.empty() || fixedCount <= target + 6)
		return;

	const float radius = std::min(0.018f, error * 1.2f) * (geometry.fidelityMode == 0 ? 0.85f : 1.f);
	const float maxMovement = std::min(0.014f, radius) * (geometry.joints.empty() ? 1.f : 0.75f);
	Bounds originalBounds;
	for (unsigned int i : source)
		originalBounds.include(xyz(geometry.positions[i]));
	result.triedUpdate = true;
	auto propose = [&](size_t goal, bool fromSource) {
		bool progressive = !fromSource && !result.updatedVertices.empty();
		auto indices = fromSource ? source : result.indices;
		auto positions = progressive ? result.updatedPositions : geometry.positions;
		auto attributes = progressive ? result.updatedAttributes : geometry.attributes;
		auto attemptLocks = locks;
		size_t count = meshopt_simplifyWithUpdate(indices.data(), indices.size(), positions[0].f,
			positions.size(), sizeof(Attr), attributes.data(), geometry.attributeStride * sizeof(float),
			weights, geometry.attributeStride, attemptLocks.data(), goal, error,
			options | meshopt_SimplifyPreserveFolds, nullptr);
		if (count < std::max(size_t(3), minimum) || count >= result.indices.size())
			return false;
		indices.resize(count);
		std::vector<unsigned char> changed(positions.size());
		if (!validateVertexUpdates(geometry, indices, locks, positions, attributes, changed, maxMovement))
			return false;

		Bounds updatedBounds;
		for (unsigned int i : indices)
			updatedBounds.include(xyz(positions[i]));
		for (int axis = 0; axis < 3; ++axis)
			if (updatedBounds.low.axis(axis) != originalBounds.low.axis(axis) ||
			    updatedBounds.high.axis(axis) != originalBounds.high.axis(axis))
				return false;

		Geometry updated = {positions, attributes, geometry.joints, geometry.weights,
			geometry.localToSource, geometry.colorStream, geometry.tangentStream, geometry.additionalUvs, geometry.attributeStride,
			geometry.hasNormals, geometry.hasUv, geometry.hasNormalMap, geometry.origin, geometry.inverseScale,
			geometry.densityTolerance, 0, 0, false, geometry.fidelityMode, false};
		const Surface candidate(updated, indices);
		if (!compareSamples(updated, candidate, geometry, original, radius * radius, attemptLocks, true, &changed) ||
		    !compareSamples(geometry, original, updated, candidate, radius * radius, attemptLocks, false, &changed))
			return false;

		result.indices.swap(indices);
		result.updatedPositions.swap(positions);
		result.updatedAttributes.swap(attributes);
		result.updatedVertices.swap(changed);
		result.trianglesSavedByUpdate = (fixedCount - count) / 3;
		result.keptOriginal = false;
		return true;
	};
	auto tryGoal = [&](size_t goal) {
		return propose(goal, false) ||
			(source.size() != result.indices.size() && propose(goal, true));
	};
	for (int pass = 0; pass < 2 && result.indices.size() > target + 6; ++pass)
	{
		size_t count = result.indices.size();
		size_t rejected = target;
		if (pass == 0)
		{
			// Start with a moderate step. Sweeping the gap from the source
			// also lets meshoptimizer choose a different set of survivors.
			rejected = std::max(target, size_t(std::ceil(count / 3 * 0.75)) * 3);
		}
		if (!tryGoal(rejected) && !bisectTarget(rejected, count, 5, tryGoal))
			break;
	}
}

// Partition high-detail islands so a difficult fold only blocks reduction in its vicinity.
// Meshlets are a working partition. The emitted GLB keeps its original primitive layout.
inline std::vector<std::vector<unsigned int> > partitionIsland(
	const std::vector<unsigned int>& indices, const std::vector<Attr>& positions, bool evenRegions = false)
{
	if (indices.size() <= (evenRegions ? 680 : 1200) * 3)
		return {indices};

	const size_t maxVertices = 96, maxTriangles = 124;
	size_t bound = meshopt_buildMeshletsBound(indices.size(), maxVertices, maxTriangles);
	std::vector<meshopt_Meshlet> meshlets(bound);
	std::vector<unsigned int> vertices(bound * maxVertices);
	std::vector<unsigned char> triangles(bound * maxTriangles * 3);
	size_t count = meshopt_buildMeshlets(meshlets.data(), vertices.data(), triangles.data(), indices.data(),
		indices.size(), positions[0].f, positions.size(), sizeof(Attr), maxVertices, maxTriangles, 0);
	if (count == 0)
		return {indices};

	std::vector<unsigned int> clusterIndices, clusterSizes;
	clusterIndices.reserve(indices.size());
	clusterSizes.reserve(count);
	for (size_t i = 0; i < count; ++i)
	{
		const auto& meshlet = meshlets[i];
		for (size_t j = 0; j < meshlet.triangle_count * 3; ++j)
			clusterIndices.push_back(vertices[meshlet.vertex_offset + triangles[meshlet.triangle_offset + j]]);
		clusterSizes.push_back(meshlet.triangle_count * 3);
	}
	if (clusterIndices.size() != indices.size())
		return {indices};
	std::vector<unsigned int> partition(count);
	size_t clustersPerRegion = evenRegions ? std::min(size_t(8), std::max(size_t(3), count / 2)) : 8;
	size_t regions = meshopt_partitionClusters(partition.data(), clusterIndices.data(), clusterIndices.size(),
		clusterSizes.data(), count, positions[0].f, positions.size(), sizeof(Attr), clustersPerRegion);

	std::vector<std::vector<unsigned int> > result(regions);
	for (size_t i = 0, offset = 0; i < count; offset += clusterSizes[i], ++i)
		result[partition[i]].insert(result[partition[i]].end(), clusterIndices.begin() + offset,
			clusterIndices.begin() + offset + clusterSizes[i]);
	return result;
}

// Use the source connected island's size instead of the artificial patch size.
// The continuous curve avoids quality jumps when an island crosses a count boundary.
inline float farStrength(float progress)
{
	return std::max(0.f, std::min(1.f, (progress - 0.4f) / 0.6f));
}

inline double islandFaceWeight(size_t faces)
{
	double weight = 0.40 + 0.60 * std::pow(double(faces) / 4096.0, 0.60);
	return std::min(1.65, weight);
}

// Keep the nearer tiers from consuming all reductions that pass validation.
inline double componentBudgetWeight(size_t faces, size_t meshFaces)
{
	double weight = islandFaceWeight(faces);
	// A dense authored mesh can contain many surface islands (for example hair
	// strands). Give those islands some of its budget without treating them as
	// one large solid part. Small standalone meshes get no extra pressure.
	double meshWeight = 0.52 * islandFaceWeight(meshFaces) * std::sqrt(double(faces) / (double(faces) + 128));
	return std::max(weight, meshWeight);
}

inline double islandRetentionFloor(double weight, float ratio)
{
	double maxRemoval = 0.95 * std::pow(1 - double(ratio), 1.35);
	return 1 - maxRemoval * std::min(1.0, std::sqrt(weight));
}

inline double islandRetention(double weight, float ratio)
{
	return std::max(std::pow(double(ratio), weight), islandRetentionFloor(weight, ratio));
}

inline float islandDensityPriority(size_t faces, float ratio)
{
	float size = std::max(0.f, std::min(1.f, float(std::log2(std::max(size_t(1), faces) / 4096.0) / 2)));
	float distance = std::max(0.f, std::min(1.f, (0.61f - ratio) / 0.5f));
	return size * distance;
}

struct BoundaryShape
{
	std::vector<unsigned char> straight;
	std::vector<unsigned char> shallow;
	std::vector<uint64_t> edges;
	std::unordered_set<uint64_t> segments;
	std::unordered_map<uint64_t, unsigned int> junctions;
	size_t loops = 0;
	bool closed = true;
	bool valid = true;
};

inline uint64_t edgeKey(unsigned int a, unsigned int b)
{
	return (uint64_t(std::min(a, b)) << 32) | std::max(a, b);
}

// Collapse collinear runs into exact endpoint pairs for comparison. Material
// and UV wedges use the same position IDs for geometry, but are locked below.
inline BoundaryShape boundaryShape(const Geometry& geometry, const std::vector<unsigned int>& indices,
	const std::vector<unsigned int>& positionIds)
{
	BoundaryShape result;
	result.straight.resize(positionIds.size());
	result.shallow.resize(positionIds.size());
	std::unordered_map<uint64_t, unsigned int> incidence;
	for (size_t i = 0; i < indices.size(); i += 3)
		for (int k = 0; k < 3; ++k)
		{
			unsigned int a = positionIds[indices[i + k]];
			unsigned int b = positionIds[indices[i + (k + 1) % 3]];
			if (a != b)
				++incidence[edgeKey(a, b)];
		}

	std::vector<std::vector<unsigned int> > neighbors(positionIds.size());
	for (const auto& edge : incidence)
		if (edge.second == 1)
		{
			unsigned int a = unsigned(edge.first >> 32), b = unsigned(edge.first);
			neighbors[a].push_back(b);
			neighbors[b].push_back(a);
			result.edges.push_back(edge.first);
		}
		else if (edge.second > 2)
			result.junctions.emplace(edge.first, edge.second);

	size_t boundaryEdges = 0;
	for (size_t i = 0; i < neighbors.size(); ++i)
	{
		boundaryEdges += neighbors[i].size();
		if (neighbors[i].empty())
			continue;
		if (neighbors[i].size() != 2)
		{
			result.closed = false;
			continue;
		}
		Vec3 a = geometry.position(neighbors[i][0]);
		Vec3 b = geometry.position(neighbors[i][1]);
		Vec3 p = geometry.position(unsigned(i));
		Vec3 line = b - a;
		Vec3 offset = cross(p - a, line);
		// Only a point on the segment may be removed. Space is normalized to the
		// region extent; the tolerance is for floating-point noise.
		result.straight[i] = dot(p - a, p - b) < 0 && dot(offset, offset) <= dot(line, line) * 1e-10f;
		result.shallow[i] = dot(unit(a - p), unit(b - p)) <= -0.94f;
	}
	if (result.closed)
	{
		std::vector<unsigned char> seen(neighbors.size());
		for (size_t i = 0; i < neighbors.size(); ++i)
		{
			if (neighbors[i].empty() || seen[i])
				continue;
			++result.loops;
			unsigned int previous = unsigned(i), current = neighbors[i][0];
			seen[i] = 1;
			while (!seen[current])
			{
				seen[current] = 1;
				unsigned int next = neighbors[current][0] == previous ? neighbors[current][1] : neighbors[current][0];
				previous = current;
				current = next;
			}
		}
	}
	std::sort(result.edges.begin(), result.edges.end());

	std::unordered_set<uint64_t> visited;
	for (size_t i = 0; i < neighbors.size(); ++i)
	{
		if (result.straight[i])
			continue;
		for (unsigned int next : neighbors[i])
		{
			unsigned int previous = unsigned(i);
			if (!visited.insert(edgeKey(previous, next)).second)
				continue;
			while (result.straight[next])
			{
				const auto& through = neighbors[next];
				unsigned int forward = through[0] == previous ? through[1] : through[0];
				if (!visited.insert(edgeKey(next, forward)).second)
				{
					result.valid = false;
					return result;
				}
				previous = next;
				next = forward;
			}
			result.segments.insert(edgeKey(unsigned(i), next));
		}
	}
	result.valid = visited.size() * 2 == boundaryEdges;
	return result;
}

inline bool coversBoundary(const Geometry& geometry, const BoundaryShape& from, const BoundaryShape& to, float tolerance)
{
	float toleranceSquared = tolerance * tolerance;
	for (uint64_t edge : from.edges)
	{
		if (std::binary_search(to.edges.begin(), to.edges.end(), edge))
			continue;
		Vec3 a = geometry.position(unsigned(edge >> 32)), b = geometry.position(unsigned(edge));
		for (float fraction : {0.f, 0.25f, 0.5f, 0.75f, 1.f})
		{
			Vec3 point = a + (b - a) * fraction;
			bool covered = false;
			for (uint64_t other : to.edges)
			{
				Vec3 c = geometry.position(unsigned(other >> 32)), d = geometry.position(unsigned(other));
				Vec3 line = d - c;
				float length = dot(line, line);
				float t = length > 0 ? std::max(0.f, std::min(1.f, dot(point - c, line) / length)) : 0;
				Vec3 offset = c + line * t - point;
				if (dot(offset, offset) <= toleranceSquared)
				{
					covered = true;
					break;
				}
			}
			if (!covered)
				return false;
		}
	}
	return true;
}

inline std::array<float, 26> outlineSupports(const Geometry& geometry, const std::vector<unsigned int>& indices, bool normalize)
{
	std::array<float, 26> result;
	result.fill(-INFINITY);
	for (unsigned int index : indices)
	{
		Vec3 p = geometry.position(index);
		size_t i = 0;
		for (int x : {-1, 0, 1})
			for (int y : {-1, 0, 1})
				for (int z : {-1, 0, 1})
					if (x || y || z)
					{
						result[i] = std::max(result[i], x * p.x + y * p.y + z * p.z);
						++i;
					}
	}
	if (normalize)
	{
		size_t i = 0;
		for (int x : {-1, 0, 1})
			for (int y : {-1, 0, 1})
				for (int z : {-1, 0, 1})
					if (x || y || z)
						result[i++] /= std::sqrt(float(x * x + y * y + z * z));
	}
	return result;
}

// Compare projected coverage before accepting wider local surface error on
// a far LOD. A fixed grid is enough to catch visible holes and outline changes.
class ProjectedOutline
{
	static const int resolution = 48;
	using Mask = std::array<uint64_t, resolution>;
	struct Vec2 { float x, y; };

	struct View
	{
		Vec3 horizontal, vertical;
		float left, bottom, scale;
		Mask source;

		Vec2 project(Vec3 point) const
		{
			return {(dot(horizontal, point) - left) * scale, (dot(vertical, point) - bottom) * scale};
		}

		Mask raster(const Surface& surface) const
		{
			Mask mask = {};
			for (const Face& face : surface.faces)
			{
				Vec2 a = project(face.position[0]), b = project(face.position[1]), c = project(face.position[2]);
				auto edge = [](Vec2 p, Vec2 q, Vec2 sample) {
					return (q.x - p.x) * (sample.y - p.y) - (q.y - p.y) * (sample.x - p.x);
				};
				float area = edge(a, b, c);
				if (std::abs(area) < 1e-6f)
					continue;
				float side = area > 0 ? 1.f : -1.f;
				int x0 = std::max(0, int(std::ceil(std::min({a.x, b.x, c.x}) - 0.5f)));
				int y0 = std::max(0, int(std::ceil(std::min({a.y, b.y, c.y}) - 0.5f)));
				int x1 = std::min(resolution - 1, int(std::floor(std::max({a.x, b.x, c.x}) - 0.5f)));
				int y1 = std::min(resolution - 1, int(std::floor(std::max({a.y, b.y, c.y}) - 0.5f)));
				for (int y = y0; y <= y1; ++y)
					for (int x = x0; x <= x1; ++x)
					{
						Vec2 sample = {x + 0.5f, y + 0.5f};
						if (edge(a, b, sample) * side >= -1e-5f && edge(b, c, sample) * side >= -1e-5f &&
						    edge(c, a, sample) * side >= -1e-5f)
							mask[y] |= uint64_t(1) << x;
					}
			}
			return mask;
		}
	};

	std::vector<View> views;
	static unsigned int count(uint64_t bits)
	{
		unsigned int result = 0;
		for (; bits; bits &= bits - 1)
			++result;
		return result;
	}

public:
	void prepare(const Surface& surface)
	{
		if (!views.empty())
			return;
		const Vec3 axes[][2] = {
			{{1, 0, 0}, {0, 1, 0}}, {{0, 0, 1}, {0, 1, 0}}, {{1, 0, 0}, {0, 0, 1}},
			{{1, 0, 1}, {0, 1, 0}}, {{1, 0, -1}, {0, 1, 0}}, {{1, 0, 0}, {0, 1, 1}},
		};
		for (const auto& axis : axes)
		{
			View view = {};
			view.horizontal = unit(axis[0]);
			view.vertical = unit(axis[1]);
			Bounds bounds;
			for (const Face& face : surface.faces)
				for (Vec3 p : face.position)
					bounds.include({dot(view.horizontal, p), dot(view.vertical, p), 0});
			float extent = bounds.extent();
			if (!(extent > 0))
				continue;
			view.left = bounds.low.x;
			view.bottom = bounds.low.y;
			view.scale = resolution / extent;
			view.source = view.raster(surface);
			views.push_back(view);
		}
	}

	bool matches(const Surface& surface, float tolerance) const
	{
		for (const View& view : views)
		{
			Mask candidate = view.raster(surface);
			unsigned int difference = 0, covered = 0;
			for (int row = 0; row < resolution; ++row)
			{
				difference += count(candidate[row] ^ view.source[row]);
				covered += count(candidate[row] | view.source[row]);
			}
			// Very thin edge-on projections can differ by a few grid samples.
			if (difference > std::max(4.f, tolerance * covered))
				return false;
		}
		return true;
	}
};

// Try the original whole-primitive collapse strategy on very distant meshes,
// while comparing its output to the immutable source in both directions.
inline void tryWholePrimitive(std::vector<unsigned int>& best, const std::vector<unsigned int>& source,
	const std::vector<std::vector<unsigned int> >& islands, const Geometry& geometry,
	const float* weights, float ratio, float error, unsigned int options, float strength, bool opaque)
{
	size_t target = std::max(size_t(3), size_t(std::ceil(source.size() / 3 * ratio)) * 3);
	if (best.size() <= target + 6)
		return;

	const Surface original(geometry, source);
	ProjectedOutline outline;
	outline.prepare(original);
	auto support = outlineSupports(geometry, source, true);
	std::vector<unsigned char> locks(geometry.positions.size());
	for (const Face& face : original.faces)
	{
		auto attrs = geometry.interpolate(face, {1.f/3, 1.f/3, 1.f/3});
		if (dot(shadingNormal(geometry, face, attrs), face.geometricNormal) < -0.12f)
			for (unsigned int vertex : face.vertices)
				locks[vertex] = meshopt_SimplifyVertex_Lock;
	}

	Geometry strict = geometry, relaxed = geometry, coarse = geometry;
	strict.densityTolerance = 2.5f;
	strict.detailTolerance = 0.25f;
	relaxed.densityTolerance = 5.f;
	relaxed.detailTolerance = 0.85f;
	coarse.densityTolerance = 2.65f;
	coarse.detailTolerance = 0.85f;
	coarse.coarseCorrespondence = true;
	float distance = 0.038f + 0.018f * strength;
	float uvProjection = distance * 0.8f;
	strict.uvProjectionRadiusSquared = relaxed.uvProjectionRadiusSquared = coarse.uvProjectionRadiusSquared =
		uvProjection * uvProjection;
	float relaxedArea = 0.34f + 0.12f * strength;
	// A small surface-area allowance for opaque LODs lets the candidate
	// lose fine shading/UV0 detail while retaining skin and secondary UVs.
	float approximateArea = opaque && !geometry.colorStream && !geometry.tangentStream ? 0.12f * strength : 0.f;
	const bool highCompression = geometry.fidelityMode == 2;
	float projectedTolerance = 0.039f + 0.017f * strength;
	float supportTolerance = 0.022f;
	if (highCompression)
	{
		distance = 0.023f + 0.02f * strength;
		strict.detailTolerance = 0.15f;
		relaxed.detailTolerance = 0.55f;
		strict.uvProjectionRadiusSquared = relaxed.uvProjectionRadiusSquared = distance * distance * 0.64f;
		relaxedArea = std::min(0.54f, relaxedArea + 0.08f);
		approximateArea = 0;
		projectedTolerance = 0.025f + 0.015f * strength;
		supportTolerance = 0.012f + 0.006f * strength;
	}

	// An isolated decoration that contributes to the visible shape must
	// still have its own triangles. A vertex shared with another island
	// cannot serve as proof that the original survives.
	std::vector<size_t> islandOwner(geometry.positions.size(), size_t(-1));
	std::vector<unsigned char> shared(geometry.positions.size()), prominent(islands.size());
	for (size_t i = 0; i < islands.size(); ++i)
	{
		Bounds bounds;
		for (unsigned int vertex : islands[i])
		{
			size_t& owner = islandOwner[vertex];
			if (owner == size_t(-1))
				owner = i;
			else if (owner != i)
				shared[vertex] = 1;
			bounds.include(geometry.position(vertex));
		}
		prominent[i] = highCompression || bounds.extent() > 0.045f;
	}

	auto assess = [&](const std::vector<unsigned int>& indices, std::vector<unsigned char>& learnedLocks) {
		const Surface reduced(geometry, indices);
		if (!outline.matches(reduced, projectedTolerance))
			return false;
		auto extents = outlineSupports(geometry, indices, true);
		for (size_t axis = 0; axis < support.size(); ++axis)
			if (support[axis] - extents[axis] > supportTolerance)
				return false;

		auto missing = prominent;
		for (unsigned int vertex : indices)
			if (!shared[vertex] && islandOwner[vertex] != size_t(-1))
				missing[islandOwner[vertex]] = 0;
		if (std::find(missing.begin(), missing.end(), 1) != missing.end())
			return false;

		const Geometry* fallback = approximateArea > 0 ? &coarse : nullptr;
		bool forward = compareSamples(strict, reduced, strict, original, distance * distance, learnedLocks,
			true, nullptr, &relaxed, relaxedArea, fallback, approximateArea);
		bool reverse = compareSamples(strict, original, strict, reduced, distance * distance, learnedLocks,
			false, nullptr, &relaxed, relaxedArea, fallback, approximateArea);
		return forward && reverse;
	};

	struct SearchWeights { float normals, uv; };
	for (SearchWeights pass : {SearchWeights{1.f, 0.25f}, SearchWeights{1.45f, 0.82f}})
	{
		std::array<float, 8> costs;
		std::copy(weights, weights + costs.size(), costs.begin());
		for (size_t i = 0; i < 3; ++i)
			costs[i] *= pass.normals;
		costs[6] *= pass.uv;
		costs[7] *= pass.uv;
		const float retentions[] = {highCompression ? 0.f : 1.f, 0.68f, 0.81f, 0.92f};
		size_t previous = best.size();
		for (float retention : retentions)
		{
			size_t goal = std::max(target, size_t(std::ceil(previous / 3 * retention)) * 3);
			if (goal >= best.size())
				continue;
			auto trialLocks = locks;
			for (int retry = 0; retry < (highCompression ? 3 : 2); ++retry)
			{
				std::vector<unsigned int> indices(source.size());
				size_t count = meshopt_simplifyWithAttributes(indices.data(), source.data(), source.size(),
					geometry.positions[0].f, geometry.positions.size(), sizeof(Attr), geometry.attributes.data(),
					geometry.attributeStride * sizeof(float), costs.data(), geometry.attributeStride,
					trialLocks.data(), goal, error * 1.45f, options | meshopt_SimplifyPreserveFolds, nullptr);
				if (count >= best.size() || count < 3)
					break;
				indices.resize(count);
				auto previousLocks = trialLocks;
				if (assess(indices, trialLocks))
				{
					best.swap(indices);
					break;
				}
				if (trialLocks == previousLocks)
					break;
			}
		}
	}
}

inline ValidationResult reduce(const std::vector<unsigned int>& source, const Geometry& geometry,
	const float* weights, const std::vector<unsigned char>& outlineLocks, std::vector<unsigned char> hardLocks,
	size_t target, float error, unsigned int options, bool deepReduction, const std::vector<unsigned char>& seamLocks,
	size_t minimum, float densityPriority, bool adaptiveBudget, float ratio, float relativeScale,
	bool sharedMaterialBorders, float lodProgress, bool independentMaterialBorders, float detailStrength)
{
	ValidationResult result;
	const Surface original(geometry, source);
	const bool highCompression = geometry.fidelityMode == 2;
	const float fidelityScale = geometry.fidelityMode == 0 ? 0.85f : 1.f;
	// The original surface remains the reference for every candidate.
	const float radius = fidelityScale * std::min(0.033f, std::min(0.018f, error * 1.2f) * (1 + 0.8f * densityPriority));
	target = std::max(target, minimum);
	// Some thin surfaces deliberately use outward shading normals on inward geometry.
	// Anchor these authored triangles so simplification cannot extend the backside into adjacent faces.
	if (geometry.hasNormals && !geometry.shadowMode)
		for (const Face& face : original.faces)
		{
			auto center = geometry.interpolate(face, {1.f/3, 1.f/3, 1.f/3});
			if (dot(face.geometricNormal, shadingNormal(geometry, face, center)) < -0.12f)
				for (unsigned int vertex : face.vertices)
					hardLocks[vertex] = meshopt_SimplifyVertex_Lock;
		}
	auto anchored = hardLocks;
	for (size_t i = 0; i < anchored.size(); ++i)
		anchored[i] |= outlineLocks[i];
	result.indices = source;
	BoundaryShape originalBoundary;
	std::vector<unsigned int> positionIds;
	std::array<float, 26> supports = {};
	bool checkBoundary = false;
	bool materialBoundaryLocked = sharedMaterialBorders;
	bool permissive = geometry.shadowMode;
	bool preserveFolds = true;
	bool normalizeOutline = false;
	float boundaryTolerance = 0;
	float outlineTolerance = 0;
	float surfaceTolerance = radius;
	Geometry validation = geometry;
	Geometry relaxedValidation = geometry;
	float relaxedAreaFraction = 0;
	ProjectedOutline projectedOutline;
	float projectedTolerance = 0;
	if (geometry.shadowMode)
	{
		// Shadows may collapse shading wedges, but every candidate must keep
		// the projected silhouette and the extrema of the original shape.
		projectedOutline.prepare(original);
		projectedTolerance = 0.015f;
		normalizeOutline = true;
		supports = outlineSupports(geometry, source, normalizeOutline);
		outlineTolerance = 0.004f * fidelityScale;
		if (!geometry.joints.empty())
		{
			std::vector<unsigned int> remap(geometry.positions.size());
			meshopt_generatePositionRemap(remap.data(), geometry.positions[0].f, remap.size(), sizeof(Attr));
			for (unsigned int i = 0; i < remap.size(); ++i)
			{
				unsigned int first = remap[i];
				if (i != first &&
				    (!std::equal(geometry.joints[i].f, geometry.joints[i].f + 4, geometry.joints[first].f) ||
				     !std::equal(geometry.weights[i].f, geometry.weights[i].f + 4, geometry.weights[first].f)))
					anchored[i] = anchored[first] = meshopt_SimplifyVertex_Lock;
			}
		}
	}

	auto attempt = [&](const std::vector<unsigned int>& input, size_t goal, std::vector<unsigned char>& locks,
		const float* costs, float proposalError) {
		if (goal >= input.size())
			return false;
		std::vector<unsigned int> candidate(input.size());
		unsigned int solverOptions = options;
		if (preserveFolds)
			solverOptions |= meshopt_SimplifyPreserveFolds;
		if (checkBoundary && !materialBoundaryLocked)
			solverOptions &= ~meshopt_SimplifyLockBorder;
		if (permissive)
			solverOptions |= meshopt_SimplifyPermissive;
		size_t count = meshopt_simplifyWithAttributes(candidate.data(), input.data(), input.size(),
			geometry.positions[0].f, geometry.positions.size(), sizeof(Attr), geometry.attributes.data(),
			geometry.attributeStride * sizeof(float), costs, geometry.attributeStride, locks.data(),
			goal, proposalError, solverOptions, nullptr);
		if (count < std::max(size_t(3), minimum) || count >= result.indices.size())
			return false;
		candidate.resize(count);
		if (checkBoundary)
		{
			BoundaryShape boundary = boundaryShape(geometry, candidate, positionIds);
			if (!boundary.valid || boundary.junctions != originalBoundary.junctions)
				return false;
			if ((materialBoundaryLocked && boundary.edges != originalBoundary.edges) ||
			    (boundary.segments != originalBoundary.segments &&
			    (boundaryTolerance <= 0 || !originalBoundary.closed || !boundary.closed ||
			     boundary.loops != originalBoundary.loops ||
			     !coversBoundary(geometry, boundary, originalBoundary, boundaryTolerance) ||
			     !coversBoundary(geometry, originalBoundary, boundary, boundaryTolerance))))
				return false;
		}
		if (outlineTolerance > 0)
		{
			auto candidateSupports = outlineSupports(geometry, candidate, normalizeOutline);
			for (size_t i = 0; i < supports.size(); ++i)
				if (supports[i] - candidateSupports[i] > outlineTolerance)
					return false;
		}
		const Surface reduced(geometry, candidate);
		const Geometry* relaxed = relaxedAreaFraction > 0 ? &relaxedValidation : nullptr;
		float distanceSquared = surfaceTolerance * surfaceTolerance;
		bool forward = compareSamples(validation, reduced, validation, original, distanceSquared, locks, true, nullptr, relaxed, relaxedAreaFraction);
		bool reverse = compareSamples(validation, original, validation, reduced, distanceSquared, locks, false, nullptr, relaxed, relaxedAreaFraction);
		if (forward && reverse && (projectedTolerance == 0 || projectedOutline.matches(reduced, projectedTolerance)))
		{
			result.indices.swap(candidate);
			return true;
		}
		result.retried = true;
		return false;
	};

	auto search = [&](const float* costs, float proposalError, int steps, const std::vector<unsigned char>& baseLocks) {
		auto locks = baseLocks;
		if (!attempt(source, target, locks, costs, proposalError) && locks != baseLocks)
			attempt(source, target, locks, costs, proposalError);
		if (permissive)
			for (float retention : {0.70f, 0.82f, 0.90f, 0.95f})
			{
				// A smaller proposal from the source can choose a better set
				// of edges than repeatedly reducing the current candidate.
				size_t goal = size_t(std::ceil(source.size() / 3 * retention)) * 3;
				if (goal <= target || goal >= result.indices.size())
					continue;
				locks = baseLocks;
				if (!attempt(source, goal, locks, costs, proposalError) && locks != baseLocks)
					attempt(source, goal, locks, costs, proposalError);
			}

		// Continue from the last accepted patch, always measuring against the immutable source.
		for (int step = 0; step < steps && result.indices.size() > target; ++step)
		{
			size_t count = result.indices.size();
			size_t medium = std::max(target, size_t(std::ceil(count / 3 * (geometry.shadowMode ? 0.65 : 0.80))) * 3);
			size_t gentle = std::max(target, size_t(std::ceil(count / 3 * 0.94)) * 3);
			locks = baseLocks;
			if (attempt(result.indices, medium, locks, costs, proposalError))
				continue;
			if (permissive)
				locks = baseLocks;
			if (attempt(result.indices, gentle, locks, costs, proposalError))
				continue;
			if (count > target + 6 && bisectTarget(medium, count, 5, [&](size_t goal) {
				// Use fresh locks at each budget. A failed large proposal
				// can touch many more vertices than a smaller valid step.
				auto trialLocks = baseLocks;
				return attempt(result.indices, goal, trialLocks, costs, proposalError);
			}))
				continue;
			if (permissive)
			{
				// For small or difficult parts, retry one collapse with local
				// locks learned from rejected candidates, then try another edge.
				locks = baseLocks;
				bool progressed = false;
				for (int trial = 0; trial < 4; ++trial)
				{
					auto previousLocks = locks;
					if (attempt(result.indices, std::max(target, count - 3), locks, costs, proposalError))
					{
						progressed = true;
						break;
					}
					if (locks == previousLocks)
						break;
				}
				if (!progressed)
					break;
				continue;
			}
			size_t fine = std::max(target, size_t(std::ceil(count / 3 * 0.98)) * 3);
			if (densityPriority <= 0 || !attempt(result.indices, fine, locks, costs, proposalError))
				break;
		}
	};
	float proposalError = error * (1 + 0.65f * densityPriority);
	search(weights, error, 8, anchored);
	if (densityPriority > 0 && result.indices.size() > target + 6)
		search(weights, proposalError, 8, anchored);

	if (!geometry.shadowMode && geometry.hasNormals && result.indices.size() > target + 6)
	{
		// Let low-curvature interiors choose cheaper collapses while pinning
		// sharp junctions and favoring survivors on curved portions.
		auto interiorLocks = anchored;
		std::vector<Vec3> firstNormals(geometry.positions.size());
		std::vector<unsigned int> remap(geometry.positions.size());
		meshopt_generatePositionRemap(remap.data(), geometry.positions[0].f, remap.size(), sizeof(Attr));
		for (const Face& face : original.faces)
			for (unsigned int vertex : face.vertices)
			{
				unsigned int position = remap[vertex];
				Vec3& normal = firstNormals[position];
				if (dot(normal, normal) == 0)
					normal = face.geometricNormal;
				else if (dot(normal, face.geometricNormal) < 0.85f)
					interiorLocks[position] |= meshopt_SimplifyVertex_Lock;
				else if (dot(normal, face.geometricNormal) < 0.98f)
					interiorLocks[position] |= meshopt_SimplifyVertex_Priority;
			}
		for (size_t i = 0; i < remap.size(); ++i)
			interiorLocks[i] |= interiorLocks[remap[i]];
		std::array<float, 8> costs;
		std::copy(weights, weights + costs.size(), costs.begin());
		for (size_t k = 0; k < 3; ++k)
			costs[k] *= highCompression ? 0.45f : 0.65f;
		search(costs.data(), proposalError * (highCompression ? 1.75f : 1.3f), 4, interiorLocks);
	}

	if (!geometry.shadowMode && deepReduction && result.indices.size() > target + 12)
	{
		// A different quadric weighting can find collapses that bypass UV-heavy failure regions.
		// The same geometric and attribute acceptance limits apply to every proposal.
		std::array<float, 8> alternate;
		std::copy(weights, weights + alternate.size(), alternate.begin());
		for (size_t k = 0; k < 3; ++k)
			alternate[k] *= 1.8f;
		for (size_t k = 6; k < 8; ++k)
			alternate[k] *= 0.35f;
		search(alternate.data(), proposalError * 1.5f, densityPriority > 0 ? 8 : 4, anchored);
		if (densityPriority > 0.6f && result.indices.size() > target + 12)
		{
			// A third proposal prioritizes the geometric structure of dense meshes.
			// It still has to pass the same surface, UV, normal and skinning checks.
			std::copy(weights, weights + alternate.size(), alternate.begin());
			for (size_t k = 6; k < 8; ++k)
				alternate[k] *= 0.16f;
			search(alternate.data(), proposalError * 1.8f, 6, anchored);
		}
	}
	if ((adaptiveBudget || geometry.shadowMode) && result.indices.size() > target)
	{
		auto trySmallStep = [&](std::vector<unsigned char> locks, float candidateError) {
			const size_t micro = std::max(target, result.indices.size() - 3);
			for (int trial = 0; trial < 5; ++trial)
			{
				auto previousLocks = locks;
				if (attempt(result.indices, micro, locks, weights, candidateError))
					break;
				// A failed appearance check adds local locks. A subsequent
				// attempt can select another edge rather than stop at the first.
				if (locks == previousLocks)
					break;
			}
		};
		if (result.indices.size() == source.size())
		{
			// Test one legal collapse before giving up on a very small or
			// constrained part. The original appearance limits are unchanged.
			trySmallStep(anchored, proposalError * 1.5f);
		}
		positionIds.resize(geometry.positions.size());
		meshopt_generatePositionRemap(positionIds.data(), geometry.positions[0].f,
			geometry.positions.size(), sizeof(Attr));
		originalBoundary = boundaryShape(geometry, source, positionIds);
		if (!originalBoundary.junctions.empty())
		{
			// Keep each edge shared by three or more faces fixed, including
			// its UV/normal wedges. The candidate must also preserve its
			// incidence, so a local junction need not disable the whole region.
			std::vector<unsigned char> junctionLocks(positionIds.size());
			for (const auto& edge : originalBoundary.junctions)
				junctionLocks[unsigned(edge.first >> 32)] = junctionLocks[unsigned(edge.first)] = meshopt_SimplifyVertex_Lock;
			for (size_t i = 0; i < positionIds.size(); ++i)
			{
				anchored[i] |= junctionLocks[positionIds[i]];
				hardLocks[i] |= junctionLocks[positionIds[i]];
			}
		}
		if (!sharedMaterialBorders && originalBoundary.valid &&
			std::find(originalBoundary.straight.begin(), originalBoundary.straight.end(), 1) != originalBoundary.straight.end())
		{
			auto borderLocks = anchored;
			for (uint64_t segment : originalBoundary.segments)
				borderLocks[unsigned(segment >> 32)] = borderLocks[unsigned(segment)] = meshopt_SimplifyVertex_Lock;
			for (size_t i = 0; i < positionIds.size(); ++i)
				if (positionIds[i] != i)
					borderLocks[i] = borderLocks[positionIds[i]] = meshopt_SimplifyVertex_Lock;
			checkBoundary = true;
			auto learnedLocks = borderLocks;
			if (!attempt(result.indices, target, learnedLocks, weights, proposalError))
			{
				trySmallStep(borderLocks, proposalError * 1.3f);
			}
			checkBoundary = false;
		}
		if (!sharedMaterialBorders && originalBoundary.valid && originalBoundary.closed && originalBoundary.loops > 0 &&
		    result.indices.size() > target &&
		    std::find(originalBoundary.shallow.begin(), originalBoundary.shallow.end(), 1) != originalBoundary.shallow.end())
		{
			auto borderLocks = anchored;
			for (uint64_t edge : originalBoundary.edges)
				for (unsigned int vertex : {unsigned(edge >> 32), unsigned(edge)})
					if (!originalBoundary.shallow[vertex])
						borderLocks[vertex] = meshopt_SimplifyVertex_Lock;
			for (size_t i = 0; i < positionIds.size(); ++i)
				if (positionIds[i] != i)
					borderLocks[i] = borderLocks[positionIds[i]] = meshopt_SimplifyVertex_Lock;

			checkBoundary = true;
			boundaryTolerance = std::min(0.003f * fidelityScale, radius * 0.23f);
			auto learnedLocks = borderLocks;
			if (!attempt(result.indices, target, learnedLocks, weights, proposalError))
			{
				size_t gentle = std::max(target, size_t(std::ceil(result.indices.size() / 3 * 0.94)) * 3);
				if (!attempt(result.indices, gentle, borderLocks, weights, proposalError * 1.15f))
					trySmallStep(borderLocks, proposalError * 1.3f);
			}
			checkBoundary = false;
		}
		if (geometry.shadowMode && originalBoundary.valid && result.indices.size() > target)
		{
			checkBoundary = true;
			boundaryTolerance = std::min(0.004f * fidelityScale, radius * 0.5f);
			search(weights, proposalError * 3.f, 8, anchored);
			checkBoundary = false;
		}
		if (!geometry.shadowMode && originalBoundary.valid && result.indices.size() > target)
		{
			std::vector<unsigned char> handedness(positionIds.size());
			for (const Face& face : original.faces)
				for (unsigned int index : face.vertices)
					handedness[positionIds[index]] |= face.uvOrientation > 0 ? 1 : face.uvOrientation < 0 ? 2 : 0;

			auto wedgeLocks = anchored;
			bool hasSplitVertices = false;
			for (unsigned int i = 0; i < positionIds.size(); ++i)
			{
				unsigned int first = positionIds[i];
				if (first == i)
					continue;
				hasSplitVertices = true;
				bool skinSeam = !geometry.joints.empty() &&
					(!std::equal(geometry.joints[i].f, geometry.joints[i].f + 4, geometry.joints[first].f) ||
					 !std::equal(geometry.weights[i].f, geometry.weights[i].f + 4, geometry.weights[first].f));
				bool additionalUvSeam = false;
				for (const Stream* stream : geometry.additionalUvs)
				{
					const float* a = stream->data[geometry.localToSource[i]].f;
					const float* b = stream->data[geometry.localToSource[first]].f;
					additionalUvSeam |= std::abs(a[0] - b[0]) > 0.018f || std::abs(a[1] - b[1]) > 0.018f;
				}
				if (handedness[first] == 3 || skinSeam || additionalUvSeam)
					wedgeLocks[i] |= meshopt_SimplifyVertex_Protect;
			}

			auto searchAppearance = [&](bool useDistance, bool lockMaterialBorders) {
				auto locks = wedgeLocks;
				materialBoundaryLocked = lockMaterialBorders;
				checkBoundary = permissive = preserveFolds = true;
				normalizeOutline = useDistance;
				outlineTolerance = 0;
				relaxedAreaFraction = 0;
				projectedTolerance = 0;
				float distance = useDistance ? lodProgress : std::max(0.f, std::min(1.f, (0.75f - ratio) / 0.55f));
				// Only closed details can trade a larger local error for their
				// small size relative to the enclosing primitive.
				float compact = source.size() < 300 && originalBoundary.edges.empty()
					? std::max(0.f, std::min(1.f, (relativeScale - 2.f) / 3.f)) : 0.f;
				surfaceTolerance = std::max(radius, std::min(0.15f, error * (2.4f + 1.25f * distance) *
					(1 + compact * (0.6f + 1.6f * distance))));
				boundaryTolerance = std::min(0.012f, error * (0.5f + 0.3f * distance));
				if (hasSplitVertices)
				{
					validation.densityTolerance = std::max(densityPriority, 0.6f + distance * (1.05f + compact));
					validation.uvProjectionRadiusSquared = surfaceTolerance * surfaceTolerance * 0.64f;
				}

				if (useDistance)
				{
					// Normalized region-space deviations may grow as the screen
					// coverage of the model falls. The outline has its own budget.
					boundaryTolerance = std::min(0.038f, error * (0.8f + 1.35f * distance));
					float surface = error * (2.7f + 3.2f * distance) * (1 + compact * distance);
					surfaceTolerance = std::max(surfaceTolerance, std::min(0.15f, surface));
					validation.densityTolerance = std::max(validation.densityTolerance,
						(0.8f + 2.3f * distance + 0.4f * compact) * (hasSplitVertices ? 1.f : 0.7f));
					if (hasSplitVertices)
					{
						float projection = std::min(surfaceTolerance * 0.8f, 0.027f + 0.045f * distance);
						validation.uvProjectionRadiusSquared = projection * projection;
					}
					// Only a bounded share of sampled surface area can use the
					// wider UV/normal limits. Facing and skin checks still apply.
					relaxedValidation.densityTolerance = validation.densityTolerance + 2.6f * distance;
					relaxedValidation.uvProjectionRadiusSquared = validation.uvProjectionRadiusSquared;
					relaxedAreaFraction = (highCompression ? 0.32f : 0.24f) * distance;
					// The same normalized disturbance has a much smaller footprint
					// on a bead or trim piece than on its surrounding garment.
					validation.detailTolerance = relaxedValidation.detailTolerance = detailStrength;
					surfaceTolerance = std::max(surfaceTolerance, 0.29f * detailStrength);
					boundaryTolerance = std::max(boundaryTolerance, 0.13f * detailStrength);
				}
				surfaceTolerance = std::max(radius, surfaceTolerance * fidelityScale);
				boundaryTolerance *= fidelityScale;
				validation.uvProjectionRadiusSquared *= fidelityScale * fidelityScale;
				relaxedValidation.uvProjectionRadiusSquared = validation.uvProjectionRadiusSquared;

				std::array<float, 8> costs;
				std::copy(weights, weights + costs.size(), costs.begin());
				for (size_t k = 0; k < 3; ++k)
					costs[k] *= highCompression ? 0.42f : 0.5f;
				for (size_t k = 6; k < 8; ++k)
					costs[k] *= useDistance ? 0.5f - 0.35f * distance : 0.5f;
				if (!useDistance)
				{
					search(costs.data(), proposalError * 2.f, 6, locks);
					search(costs.data(), proposalError * 4.f, 8, locks);
				}

				if ((!hasSplitVertices && !useDistance) || result.indices.size() <= target)
					return;

				// Preserve the outline as a bounded shape rather than as a fixed
				// set of vertices. Material and partition anchors remain pinned.
				for (size_t i = 0; i < locks.size(); ++i)
					if (outlineLocks[i] && !hardLocks[i])
						locks[i] = (locks[i] & ~meshopt_SimplifyVertex_Lock) | meshopt_SimplifyVertex_Priority;
				supports = outlineSupports(geometry, source, normalizeOutline);
				outlineTolerance = (useDistance ? 1.f : 1.7f) * (boundaryTolerance + compact * 0.02f * distance);
				if (highCompression)
				{
					projectedOutline.prepare(original);
					projectedTolerance = 0.022f + 0.014f * distance;
					outlineTolerance = std::min(outlineTolerance, 0.016f);
				}
				preserveFolds = false;
				search(costs.data(), proposalError * 4.f, 8, locks);
				for (size_t k = 0; k < 3; ++k)
					costs[k] *= 0.5f;
				search(costs.data(), proposalError * (compact > 0 ? 10.f : 7.f), 8, locks);

				float coarse = useDistance ? farStrength(distance) : 0.f;
				if (coarse > 0 && geometry.fidelityMode != 0 && source.size() >= 96 * 3 && result.indices.size() > target)
				{
					// The ordinary candidates stay available. This additional pass
					// can smooth local folds if the six projected outlines agree.
					projectedOutline.prepare(original);
					if (!highCompression)
						projectedTolerance = 0.035f + 0.025f * coarse;
					float tolerance = std::max(detailStrength, 0.65f * coarse);
					validation.detailTolerance = relaxedValidation.detailTolerance = tolerance;
					surfaceTolerance = std::max(surfaceTolerance, 0.075f + 0.065f * coarse);
					relaxedAreaFraction = std::max(relaxedAreaFraction, (highCompression ? 0.46f : 0.39f) * coarse);
					for (size_t k = 6; k < 8; ++k)
						costs[k] *= 0.4f;
					search(costs.data(), proposalError * (7.f + 4.f * coarse), 8, locks);
				}
			};

			// Keep the best verified candidate from the regular pass before
			// broadening the search with the distance-based budgets.
			searchAppearance(false, sharedMaterialBorders);
			if (lodProgress >= 0 && result.indices.size() > target)
			{
				searchAppearance(true, sharedMaterialBorders);
				if (independentMaterialBorders && result.indices.size() > target)
					searchAppearance(true, false);
			}
		}
	}
	result.keptOriginal = result.indices.size() == source.size();
	if (!seamLocks.empty())
	{
		for (size_t i = 0; i < anchored.size(); ++i)
			anchored[i] |= seamLocks[i];
		tryVertexUpdates(result, source, geometry, original, weights, anchored, target, minimum, error, options);
	}
	return result;
}
}
