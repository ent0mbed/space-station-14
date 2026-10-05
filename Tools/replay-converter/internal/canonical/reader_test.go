package canonical

import (
	"bytes"
	"encoding/binary"
	"testing"
)

func TestReader(t *testing.T) {
	var stream bytes.Buffer
	_ = binary.Write(&stream, binary.LittleEndian, Magic)
	_ = binary.Write(&stream, binary.LittleEndian, Version)
	stream.WriteByte(byte(KindHeader))
	_ = binary.Write(&stream, binary.LittleEndian, uint32(3))
	stream.WriteString("abc")

	reader, err := NewReader(&stream)
	if err != nil {
		t.Fatal(err)
	}
	record, err := reader.Next()
	if err != nil {
		t.Fatal(err)
	}
	if record.Kind != KindHeader || string(record.Payload) != "abc" {
		t.Fatalf("unexpected record: %#v", record)
	}
}

func TestReaderRejectsOversizedRecord(t *testing.T) {
	var stream bytes.Buffer
	_ = binary.Write(&stream, binary.LittleEndian, Magic)
	_ = binary.Write(&stream, binary.LittleEndian, Version)
	stream.WriteByte(byte(KindTick))
	_ = binary.Write(&stream, binary.LittleEndian, MaxRecordSize+1)

	reader, err := NewReader(&stream)
	if err != nil {
		t.Fatal(err)
	}
	if _, err = reader.Next(); err == nil {
		t.Fatal("expected oversized record to fail")
	}
}
