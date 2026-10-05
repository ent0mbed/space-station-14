package canonical

import (
	"encoding/binary"
	"errors"
	"fmt"
	"io"
)

const (
	Magic         = uint32(0x31505257)
	Version       = uint16(1)
	MaxRecordSize = uint32(64 * 1024 * 1024)
)

type Kind byte

const (
	KindHeader   Kind = 1
	KindTick     Kind = 2
	KindUpsert   Kind = 3
	KindDelete   Kind = 4
	KindResource Kind = 5
	KindEnd      Kind = 255
)

type Record struct {
	Kind    Kind
	Payload []byte
}

type Reader struct {
	r io.Reader
}

func NewReader(r io.Reader) (*Reader, error) {
	var header [6]byte
	if _, err := io.ReadFull(r, header[:]); err != nil {
		return nil, fmt.Errorf("read canonical header: %w", err)
	}
	if binary.LittleEndian.Uint32(header[:4]) != Magic {
		return nil, errors.New("invalid canonical stream magic")
	}
	if version := binary.LittleEndian.Uint16(header[4:]); version != Version {
		return nil, fmt.Errorf("unsupported canonical stream version %d", version)
	}
	return &Reader{r: r}, nil
}

func (r *Reader) Next() (Record, error) {
	var header [5]byte
	if _, err := io.ReadFull(r.r, header[:]); err != nil {
		return Record{}, err
	}

	size := binary.LittleEndian.Uint32(header[1:])
	if size > MaxRecordSize {
		return Record{}, fmt.Errorf("canonical record exceeds limit: %d", size)
	}

	payload := make([]byte, size)
	if _, err := io.ReadFull(r.r, payload); err != nil {
		return Record{}, fmt.Errorf("read canonical record payload: %w", err)
	}
	return Record{Kind: Kind(header[0]), Payload: payload}, nil
}
